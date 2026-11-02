export interface AgentProfile { id:string; name:string; command:string; kind:string; env:Record<string,string>; resumeArguments?:string|null; builtIn:boolean; available:boolean }
export interface SessionInfo { id:string; workspaceId:string; title:string; agentId:string; cwd:string; processId:number; running:boolean; exitCode?:number|null; startedAt:string; lastOutputAt?:string|null; nativeSessionId?:string|null; cols:number; rows:number; bytesRead:number }
export interface Chunk { sessionId:string; sequence:number; data:string }
export interface Replay extends Chunk { truncated:boolean }
export interface Pane { type:'pane'; id:string; tabs:string[]; activeTabId:string|null }
export interface Split { type:'split'; id:string; orientation:'horizontal'|'vertical'; fraction:number; first:PaneNode; second:PaneNode }
export type PaneNode = Pane|Split;
export interface Workspace { id:string; name:string; path:string; pinned:boolean; tree:PaneNode; activePaneId:string }
export interface AppState { schemaVersion:1; workspaces:Workspace[]; activeWorkspaceId:string|null; recent:{path:string;name:string;lastOpened:string}[]; theme:'dark'|'light'; fontSize:number; sidebarWidth:number; sessionSnapshots:SessionInfo[] }
export interface WorkspaceInfo { path:string; git:{isRepository:boolean;branch:string|null;changedFiles:number;added:number;deleted:number;error?:string|null};node:string|null;python:string|null;environment:string|null }
export interface FileEntry { name:string;path:string;isDirectory:boolean;size:number|null }
export interface NativeSession { id:string;agentId:string;cwd:string;modifiedAt:string;title:string|null }
export interface Bootstrap { state:Partial<AppState>|null; agents:AgentProfile[]; sessions:SessionInfo[]; appVersion:string; homePath:string; warnings?:string[]; windowsBuild?:number }
