#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod broker;

use broker::{BrokerProcess, Connection};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{
    collections::HashMap,
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, AtomicUsize, Ordering},
        Arc, Mutex,
    },
    time::{Duration, Instant},
};
use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    AppHandle, Emitter, Manager, Theme, WebviewUrl, WebviewWindow, WebviewWindowBuilder,
};
use tauri_plugin_clipboard_manager::ClipboardExt;
use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};
use tauri_plugin_global_shortcut::{Code, GlobalShortcutExt, Modifiers, Shortcut, ShortcutState};
use tauri_plugin_notification::NotificationExt;
use tauri_plugin_opener::OpenerExt;

struct Desktop {
    broker: BrokerProcess,
    connections: Mutex<HashMap<String, Arc<Connection>>>,
    connect_lock: tokio::sync::Mutex<()>,
    exiting: AtomicBool,
    next_window: AtomicUsize,
    theme: Mutex<Theme>,
    debug: bool,
    debug_port: u16,
    started: Instant,
    broker_pid: AtomicUsize,
}

impl Desktop {
    async fn connection(&self, app: &AppHandle, label: &str) -> Result<Arc<Connection>, String> {
        let _guard = self.connect_lock.lock().await;
        if let Some(connection) = self
            .connections
            .lock()
            .unwrap()
            .get(label)
            .filter(|c| c.is_connected())
        {
            return Ok(connection.clone());
        }
        let connection = Connection::connect(&self.broker, app.clone(), label.into()).await?;
        self.connections
            .lock()
            .unwrap()
            .insert(label.into(), connection.clone());
        Ok(connection)
    }

    fn log(&self, text: &str) {
        use std::io::Write;
        if let Ok(mut file) = std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(self.broker.identity.data.join("desktop.log"))
        {
            let _ = writeln!(
                file,
                "{} {text}",
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_secs()
            );
        }
    }
}

fn show_main(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.unminimize();
        let _ = window.show();
        let _ = window.set_focus();
    }
}

fn make_window(app: &AppHandle, label: &str, monitor: bool) -> Result<WebviewWindow, String> {
    let desktop = app.state::<Desktop>();
    let page = if monitor {
        "index.html?monitor=1"
    } else {
        "index.html"
    };
    let mut builder = WebviewWindowBuilder::new(app, label, WebviewUrl::App(page.into()))
        .title(if monitor {
            "Weirdo · Agent Monitor"
        } else {
            "Weirdo"
        })
        .decorations(false)
        .resizable(true)
        .shadow(true)
        .inner_size(
            if monitor { 580.0 } else { 1320.0 },
            if monitor { 640.0 } else { 880.0 },
        )
        .min_inner_size(
            if monitor { 420.0 } else { 780.0 },
            if monitor { 350.0 } else { 480.0 },
        )
        .theme(Some(*desktop.theme.lock().unwrap()))
        .background_color(tauri::window::Color(33, 37, 43, 255))
        .data_directory(desktop.broker.identity.data.join("webview"))
        .disable_drag_drop_handler()
        .center()
        .on_navigation(|url| {
            matches!(
                url.host_str(),
                Some("tauri.localhost" | "localhost" | "127.0.0.1")
            ) || url.scheme() == "tauri"
        });
    if desktop.debug {
        builder = builder.additional_browser_args(&format!(
            "--remote-debugging-port={} --remote-debugging-address=127.0.0.1",
            desktop.debug_port
        ));
    }
    let window = builder.build().map_err(|e| e.to_string())?;
    let watched = window.clone();
    window.on_window_event(move |event| {
        match event {
            tauri::WindowEvent::CloseRequested { api, .. } => {
                if matches!(watched.label(), "main" | "monitor") && !watched.state::<Desktop>().exiting.load(Ordering::Acquire) {
                    api.prevent_close();
                    let _ = watched.hide();
                }
            }
            tauri::WindowEvent::Resized(_) => {
                let _ = watched.emit("host-event", json!({"type":"window.maximized","payload":{"maximized":watched.is_maximized().unwrap_or(false)}}));
            }
            tauri::WindowEvent::Destroyed => {
                if let Some(connection) = watched.state::<Desktop>().connections.lock().unwrap().remove(watched.label()) {
                    connection.disconnect();
                }
            }
            _ => {}
        }
    });
    Ok(window)
}

fn show_monitor(app: &AppHandle) -> Result<(), String> {
    let window = match app.get_webview_window("monitor") {
        Some(window) => window,
        None => make_window(app, "monitor", true)?,
    };
    window.show().map_err(|e| e.to_string())?;
    window.set_focus().map_err(|e| e.to_string())
}

fn close_interface(app: &AppHandle) {
    app.state::<Desktop>()
        .exiting
        .store(true, Ordering::Release);
    app.exit(0);
}

async fn quit(app: AppHandle) -> Result<(), String> {
    let dialog_app = app.clone();
    let confirmed = tauri::async_runtime::spawn_blocking(move || {
        dialog_app
            .dialog()
            .message("停止所有运行中的终端并退出 Weirdo？Agent 原生对话可在下次启动时恢复。")
            .title("退出 Weirdo")
            .kind(MessageDialogKind::Warning)
            .buttons(MessageDialogButtons::YesNo)
            .blocking_show()
    })
    .await
    .map_err(|e| e.to_string())?;
    if confirmed {
        let desktop = app.state::<Desktop>();
        let connection = desktop.connection(&app, "main").await?;
        connection.request("broker.shutdown", json!({})).await?;
        close_interface(&app);
    }
    Ok(())
}

#[tauri::command]
async fn host_request(
    app: AppHandle,
    window: WebviewWindow,
    kind: String,
    payload: Value,
) -> Result<Value, String> {
    let desktop = app.state::<Desktop>();
    match kind.as_str() {
        "dialog.folder" => {
            let directory = payload["path"].as_str().map(PathBuf::from);
            let path = tauri::async_runtime::spawn_blocking(move || {
                let mut picker = app
                    .dialog()
                    .file()
                    .set_parent(&window)
                    .set_title("打开工作区");
                if let Some(directory) = directory.filter(|path| path.is_absolute()) {
                    picker = picker.set_directory(directory);
                }
                picker.blocking_pick_folder()
            })
            .await
            .map_err(|e| e.to_string())?;
            return Ok(path
                .map(|p| Value::String(p.to_string()))
                .unwrap_or(Value::Null));
        }
        "clipboard.read" => {
            return app
                .clipboard()
                .read_text()
                .map(Value::String)
                .map_err(|e| e.to_string())
        }
        "clipboard.write" => app
            .clipboard()
            .write_text(payload["text"].as_str().unwrap_or_default())
            .map_err(|e| e.to_string())?,
        "path.open" => {
            let path = payload["path"].as_str().ok_or("缺少文件路径。")?;
            if !PathBuf::from(path).exists() {
                return Err("文件或目录已不存在。".into());
            }
            app.opener()
                .open_path(path, None::<&str>)
                .map_err(|e| e.to_string())?;
        }
        "window.show" => show_main(&app),
        "window.hide" | "window.close" => {
            if matches!(window.label(), "main" | "monitor") {
                window.hide()
            } else {
                window.destroy()
            }
            .map_err(|e| e.to_string())?;
        }
        "window.minimize" => window.minimize().map_err(|e| e.to_string())?,
        "window.maximize" => {
            if window.is_maximized().map_err(|e| e.to_string())? {
                window.unmaximize()
            } else {
                window.maximize()
            }
            .map_err(|e| e.to_string())?;
            return Ok(Value::Bool(window.is_maximized().unwrap_or(false)));
        }
        "window.isMaximized" => {
            return window
                .is_maximized()
                .map(Value::Bool)
                .map_err(|e| e.to_string())
        }
        "window.drag" => window.start_dragging().map_err(|e| e.to_string())?,
        "window.monitor" => show_monitor(&app)?,
        "window.new" => {
            let number = desktop.next_window.fetch_add(1, Ordering::Relaxed);
            make_window(&app, &format!("workspace-{number}"), false)?;
        }
        "window.theme" => {
            let theme = if payload["theme"] == "light" {
                Theme::Light
            } else {
                Theme::Dark
            };
            *desktop.theme.lock().unwrap() = theme;
            for window in app.webview_windows().values() {
                window.set_theme(Some(theme)).map_err(|e| e.to_string())?;
            }
        }
        "window.ready" => {
            if window.label() == "main" {
                let startup = json!({
                    "processId":std::process::id(), "elapsedMs":desktop.started.elapsed().as_secs_f64() * 1000.0,
                    "executable":std::env::current_exe().map_err(|e|e.to_string())?,
                    "version":app.package_info().version.to_string(), "debug":desktop.debug,
                    "host":"Tauri 2", "brokerPid":desktop.broker_pid.load(Ordering::Relaxed)
                });
                std::fs::write(
                    desktop.broker.identity.data.join("startup.json"),
                    startup.to_string(),
                )
                .map_err(|e| e.to_string())?;
            }
        }
        _ => {
            let connection = desktop.connection(&app, window.label()).await?;
            let payload = if matches!(kind.as_str(), "updates.install" | "performance") {
                json!({"hostPid":std::process::id()})
            } else {
                payload
            };
            let response = connection.request(&kind, payload.clone()).await;
            let mut result = match response {
                Err(error)
                    if kind == "workspace.resolve"
                        && error.starts_with("Unknown service command:") =>
                {
                    // A live 0.2.0 broker can stay attached while its Agents finish.
                    let path = payload["path"]
                        .as_str()
                        .unwrap_or_default()
                        .trim()
                        .trim_matches('"')
                        .trim_matches('\'');
                    let inspection = connection
                        .request("workspace.inspect", json!({"path":path}))
                        .await?;
                    let path = inspection["path"].as_str().ok_or("后台未返回项目路径。")?;
                    let name = std::path::Path::new(path)
                        .file_name()
                        .and_then(|name| name.to_str())
                        .unwrap_or(path);
                    json!({"path":path,"name":name})
                }
                result => result?,
            };
            if kind == "bootstrap" {
                result["desktopHost"] = json!("Tauri 2");
                let broker_version = result["appVersion"]
                    .as_str()
                    .unwrap_or_default()
                    .to_string();
                result["brokerVersion"] = json!(broker_version);
                if broker_version != app.package_info().version.to_string() {
                    if let Some(warnings) = result["warnings"].as_array_mut() {
                        warnings.push(json!(format!("后台终端服务仍为 {broker_version}，现有任务继续运行。任务结束后从托盘完整退出，再启动即可加载新版服务。")));
                    }
                }
                result["appVersion"] = json!(app.package_info().version.to_string());
                result["hostPid"] = json!(std::process::id());
                desktop.broker_pid.store(
                    result["brokerPid"].as_u64().unwrap_or_default() as usize,
                    Ordering::Relaxed,
                );
            }
            if kind == "updates.install" && result["started"] == true {
                close_interface(&app);
            }
            return Ok(result);
        }
    }
    Ok(Value::Null)
}

fn install_tray(app: &AppHandle) -> tauri::Result<()> {
    let show = MenuItem::with_id(app, "show", "打开 Weirdo", true, None::<&str>)?;
    let monitor = MenuItem::with_id(app, "monitor", "Agent 活动监视器", true, None::<&str>)?;
    let separator = PredefinedMenuItem::separator(app)?;
    let keep = MenuItem::with_id(app, "keep", "关闭界面，保留终端", true, None::<&str>)?;
    let quit_item = MenuItem::with_id(app, "quit", "停止全部终端并退出", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&show, &monitor, &separator, &keep, &quit_item])?;
    TrayIconBuilder::with_id("weirdo")
        .icon(app.default_window_icon().unwrap().clone())
        .tooltip("Weirdo · 终端工作台")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id.as_ref() {
            "show" => show_main(app),
            "monitor" => {
                if let Err(error) = show_monitor(app) {
                    app.state::<Desktop>().log(&error);
                }
            }
            "keep" => close_interface(app),
            "quit" => {
                let app = app.clone();
                tauri::async_runtime::spawn(async move {
                    if let Err(error) = quit(app.clone()).await {
                        app.state::<Desktop>().log(&error);
                    }
                });
            }
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if matches!(
                event,
                TrayIconEvent::Click {
                    button: MouseButton::Left,
                    button_state: MouseButtonState::Up,
                    ..
                }
            ) {
                show_main(tray.app_handle());
            }
        })
        .build(app)?;
    Ok(())
}

fn main() {
    let started = Instant::now();
    let args: Vec<String> = std::env::args().collect();
    let data = args
        .iter()
        .position(|a| a == "--data-dir")
        .and_then(|i| args.get(i + 1))
        .map(PathBuf::from);
    let debug = args.iter().any(|a| a == "--devtools");
    let debug_port = args
        .iter()
        .position(|a| a == "--devtools-port")
        .and_then(|i| args.get(i + 1))
        .and_then(|value| value.parse::<u16>().ok())
        .filter(|port| *port >= 1024)
        .unwrap_or(9334);
    let mut context = tauri::generate_context!();
    if let Some(directory) = &data {
        // Isolated test/dev profiles must never activate a window with another profile's data.
        std::fs::create_dir_all(directory).expect("The profile directory could not be created");
        let canonical =
            std::fs::canonicalize(directory).expect("The profile path could not be resolved");
        let default =
            PathBuf::from(std::env::var_os("LOCALAPPDATA").expect("LOCALAPPDATA is unavailable"))
                .join("Weirdo");
        let default = std::fs::canonicalize(&default).unwrap_or(default);
        let normalize = |path: &std::path::Path| {
            path.to_string_lossy()
                .trim_start_matches(r"\\?\")
                .to_uppercase()
        };
        let normalized = normalize(&canonical);
        if normalized != normalize(&default) {
            let digest = Sha256::digest(normalized.as_bytes());
            let suffix: String = digest[..12]
                .iter()
                .map(|byte| format!("{byte:02x}"))
                .collect();
            context.config_mut().identifier = format!("app.weirdo.desktop.profile-{suffix}");
        }
    }
    let builder = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, args, _| {
            if args.iter().any(|a| a == "--new-window") {
                let number = app.state::<Desktop>().next_window.fetch_add(1, Ordering::Relaxed);
                let _ = make_window(app, &format!("workspace-{number}"), false);
            } else { show_main(app); }
        }))
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_clipboard_manager::init())
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_global_shortcut::Builder::new().with_handler(|app, shortcut, event| {
            if event.state() != ShortcutState::Pressed { return; }
            if shortcut.matches(Modifiers::CONTROL | Modifiers::ALT | Modifiers::SHIFT, Code::KeyW) {
                let _ = show_monitor(app);
            } else if let Some(window) = app.get_webview_window("main") {
                if window.is_visible().unwrap_or(false) && window.is_focused().unwrap_or(false) { let _ = window.hide(); }
                else { show_main(app); }
            }
        }).build())
        .invoke_handler(tauri::generate_handler![host_request])
        .setup(move |app| {
            let resource_dir = app.path().resource_dir()?;
            let mut broker_path = resource_dir.join("Weirdo.Broker.exe");
            if !broker_path.exists() { broker_path = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../dist/native-broker/Weirdo.Broker.exe"); }
            let broker = BrokerProcess::initialize(broker_path, data.as_deref()).map_err(std::io::Error::other)?;
            app.manage(Desktop { broker, connections: Mutex::new(HashMap::new()), connect_lock: tokio::sync::Mutex::new(()), exiting: AtomicBool::new(false), next_window: AtomicUsize::new(1), theme: Mutex::new(Theme::Dark), debug, debug_port, started, broker_pid: AtomicUsize::new(0) });
            make_window(app.handle(), "main", false).map_err(std::io::Error::other)?;
            install_tray(app.handle())?;
            let shortcuts = [Shortcut::new(Some(Modifiers::CONTROL | Modifiers::ALT), Code::KeyW), Shortcut::new(Some(Modifiers::CONTROL | Modifiers::ALT | Modifiers::SHIFT), Code::KeyW)];
            if app.config().identifier == "app.weirdo.desktop" {
                if let Err(error) = app.global_shortcut().register_multiple(shortcuts) { app.state::<Desktop>().log(&format!("全局快捷键注册失败。{error}")); }
            }
            let updater_app = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                tokio::time::sleep(Duration::from_secs(30)).await;
                let mut notified = String::new();
                loop {
                    let desktop = updater_app.state::<Desktop>();
                    if let Ok(connection) = desktop.connection(&updater_app, "main").await {
                        if let Ok(update) = connection.request("updates.check", json!({})).await {
                            let version = update["version"].as_str().unwrap_or_default();
                            if update["available"] == true && version != notified {
                                notified = version.into();
                                let _ = updater_app.emit("host-event", json!({"type":"updates.available","payload":{"version":version}}));
                                let _ = updater_app.notification().builder().title("Weirdo 有可用更新").body(format!("版本 {version} 已可用，请在设置中安装。")).show();
                            }
                        }
                    }
                    tokio::time::sleep(Duration::from_secs(6 * 60 * 60)).await;
                }
            });
            Ok(())
        });
    let app = builder.build(context).expect("Weirdo could not start");
    app.run(|app, event| {
        if let tauri::RunEvent::ExitRequested { api, .. } = event {
            if !app.state::<Desktop>().exiting.load(Ordering::Acquire) {
                api.prevent_exit();
            }
        }
    });
}
