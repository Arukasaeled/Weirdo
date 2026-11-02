use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    os::windows::process::CommandExt,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Mutex,
    },
    time::{Duration, Instant},
};
use tauri::{AppHandle, Emitter, Manager};
use tauri_plugin_notification::NotificationExt;
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader, WriteHalf},
    net::windows::named_pipe::{ClientOptions, NamedPipeClient},
    sync::{oneshot, Mutex as AsyncMutex, Notify},
};

const CREATE_NO_WINDOW: u32 = 0x08000000;

#[derive(Deserialize, Clone)]
pub struct Identity {
    pub pipe: String,
    pub data: PathBuf,
}

pub struct BrokerProcess {
    pub executable: PathBuf,
    pub identity: Identity,
}

impl BrokerProcess {
    pub fn initialize(executable: PathBuf, data: Option<&Path>) -> Result<Self, String> {
        let mut command = Command::new(&executable);
        command.arg("--identity").creation_flags(CREATE_NO_WINDOW);
        if let Some(data) = data {
            command.arg("--data-dir").arg(data);
        }
        let result = command
            .output()
            .map_err(|e| format!("无法启动终端服务 {}。{e}", executable.display()))?;
        if !result.status.success() {
            return Err(format!(
                "终端服务初始化失败。{}",
                String::from_utf8_lossy(&result.stderr)
            ));
        }
        let identity =
            serde_json::from_slice(&result.stdout).map_err(|e| format!("终端服务响应无效。{e}"))?;
        Ok(Self {
            executable,
            identity,
        })
    }

    fn start(&self) -> Result<Child, String> {
        Command::new(&self.executable)
            .arg("--broker")
            .arg("--data-dir")
            .arg(&self.identity.data)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .map_err(|e| e.to_string())
    }
}

type Response = oneshot::Sender<Result<Value, String>>;

pub struct Connection {
    writer: AsyncMutex<WriteHalf<NamedPipeClient>>,
    pending: Mutex<HashMap<String, Response>>,
    connected: AtomicBool,
    cancel: Arc<Notify>,
}

impl Connection {
    pub fn is_connected(&self) -> bool {
        self.connected.load(Ordering::Acquire)
    }

    pub fn disconnect(&self) {
        self.connected.store(false, Ordering::Release);
        self.cancel.notify_one();
        for (_, response) in self.pending.lock().unwrap().drain() {
            let _ = response.send(Err("窗口已关闭。".into()));
        }
    }

    pub async fn connect(
        process: &BrokerProcess,
        app: AppHandle,
        label: String,
    ) -> Result<Arc<Self>, String> {
        let pipe_name = format!(r"\\.\pipe\{}", process.identity.pipe);
        // The first migration can copy many encrypted native conversation files.
        let deadline = Instant::now() + Duration::from_secs(120);
        let mut started: Option<Child> = None;
        let pipe = loop {
            match ClientOptions::new().open(&pipe_name) {
                Ok(pipe) => break pipe,
                Err(error) => {
                    // Retry only missing/busy pipes. A permission error is actionable.
                    if !matches!(error.raw_os_error(), Some(2 | 231)) || Instant::now() >= deadline
                    {
                        return Err(format!("无法连接本机终端服务。{error}"));
                    }
                    if let Some(child) = started.as_mut() {
                        if let Some(status) = child.try_wait().map_err(|e| e.to_string())? {
                            if !status.success() {
                                return Err(format!("终端服务启动失败（{status}）。请检查本机应用数据中的 errors.log。"));
                            }
                        }
                    } else {
                        started = Some(process.start()?);
                    }
                    tokio::time::sleep(Duration::from_millis(80)).await;
                }
            }
        };
        let (reader, writer) = tokio::io::split(pipe);
        let connection = Arc::new(Self {
            writer: AsyncMutex::new(writer),
            pending: Mutex::new(HashMap::new()),
            connected: AtomicBool::new(true),
            cancel: Arc::new(Notify::new()),
        });
        let weak = Arc::downgrade(&connection);
        let cancel = connection.cancel.clone();
        tauri::async_runtime::spawn(async move {
            let mut reader = BufReader::with_capacity(65536, reader);
            let mut line = String::new();
            loop {
                line.clear();
                let read = tokio::select! {
                    _ = cancel.notified() => break,
                    read = reader.read_line(&mut line) => read,
                };
                match read {
                    Ok(0) | Err(_) => break,
                    Ok(_) => {}
                }
                let Some(connection) = weak.upgrade() else {
                    break;
                };
                let Ok(message) = serde_json::from_str::<Value>(&line) else {
                    break;
                };
                if let Some(id) = message["id"].as_str() {
                    let response = connection.pending.lock().unwrap().remove(id);
                    if let Some(response) = response {
                        let result = if message["ok"].as_bool() == Some(true) {
                            Ok(message["result"].clone())
                        } else {
                            Err(message["error"]
                                .as_str()
                                .unwrap_or("操作未完成")
                                .to_string())
                        };
                        let _ = response.send(result);
                    }
                } else {
                    let _ = app.emit_to(&label, "host-event", &message);
                    if message["type"] == "broker.stopping" {
                        super::close_interface(&app);
                    }
                    // One subscriber owns native notifications, avoiding duplicates in multiple windows.
                    if label == "main" && message["type"] == "terminal.exit" {
                        let focused = app
                            .webview_windows()
                            .values()
                            .any(|w| w.is_focused().unwrap_or(false));
                        if !focused {
                            let info = &message["payload"];
                            let title = info["title"].as_str().unwrap_or("终端");
                            let status = info["exitCode"]
                                .as_i64()
                                .map(|n| n.to_string())
                                .unwrap_or_else(|| "未知".into());
                            let _ = app
                                .notification()
                                .builder()
                                .title(format!("Weirdo · {title}"))
                                .body(format!("进程已退出，退出码 {status}。"))
                                .show();
                        }
                    }
                }
            }
            if let Some(connection) = weak.upgrade() {
                connection.connected.store(false, Ordering::Release);
                for (_, response) in connection.pending.lock().unwrap().drain() {
                    let _ = response.send(Err(
                        "终端服务已断开，请重新打开 Weirdo。已保存的会话可恢复。".into(),
                    ));
                }
                let _ = app.emit_to(
                    &label,
                    "host-event",
                    json!({"type":"broker.disconnected","payload":{}}),
                );
            }
        });
        Ok(connection)
    }

    pub async fn request(&self, kind: &str, payload: Value) -> Result<Value, String> {
        let id = uuid::Uuid::new_v4().simple().to_string();
        let (send, receive) = oneshot::channel();
        self.pending.lock().unwrap().insert(id.clone(), send);
        let mut message = serde_json::to_vec(&json!({"id":id,"type":kind,"payload":payload}))
            .map_err(|e| e.to_string())?;
        message.push(b'\n');
        let write = self.writer.lock().await.write_all(&message).await;
        if let Err(error) = write {
            self.pending.lock().unwrap().remove(&id);
            self.connected.store(false, Ordering::Release);
            return Err(error.to_string());
        }
        let seconds = match kind {
            "updates.install" => 600,
            "updates.check" => 120,
            _ => 40,
        };
        let result = tokio::time::timeout(Duration::from_secs(seconds), receive).await;
        self.pending.lock().unwrap().remove(&id);
        result
            .map_err(|_| format!("{kind} 请求超时。"))?
            .map_err(|_| "终端服务已断开。".to_string())?
    }
}
