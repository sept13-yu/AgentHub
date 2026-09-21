use crate::{bootstrap, AppState};
use std::sync::atomic::Ordering;
use tauri::{AppHandle, Manager, Theme, WebviewWindow};

#[tauri::command]
pub fn set_theme(window: WebviewWindow, theme: String) -> Result<(), String> {
    if window.label() != "main" {
        return Err("set_theme 仅允许主窗".into());
    }
    let t = match theme.as_str() {
        "light" => Theme::Light,
        "dark" => Theme::Dark,
        _ => return Err("theme 只能是 light/dark".into()),
    };
    window.set_theme(Some(t)).map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn pick_folder(
    app: AppHandle,
    window: WebviewWindow,
    initial_path: Option<String>,
) -> Result<Option<String>, String> {
    if window.label() != "main" {
        return Err("pick_folder 仅允许主窗".into());
    }
    use tauri_plugin_dialog::DialogExt;
    let mut builder = app.dialog().file().set_title("选择 Agent 文档资料目录");
    if let Some(p) = initial_path.as_deref() {
        if !p.trim().is_empty() {
            builder = builder.set_directory(p);
        }
    }
    let (tx, rx) = std::sync::mpsc::channel::<Option<String>>();
    builder.pick_folder(move |folder| {
        let _ = tx.send(folder.map(|p| p.to_string()));
    });
    // 不在 async worker 上阻塞 recv
    tauri::async_runtime::spawn_blocking(move || rx.recv())
        .await
        .map_err(|e| format!("目录选择任务失败：{e}"))?
        .map_err(|e| format!("目录选择失败：{e}"))
}

#[tauri::command]
pub fn retry(app: AppHandle, window: WebviewWindow) -> Result<(), String> {
    if window.label() != "bootstrap" {
        return Err("retry 仅允许失败页调用".into());
    }
    if let Some(state) = app.try_state::<AppState>() {
        // 只做快速预检；真正串行化由 bootstrap 入口的 CAS 保证
        if state.bootstrap_in_flight.load(Ordering::SeqCst) {
            return Ok(());
        }
    }
    std::thread::spawn(move || bootstrap(&app));
    Ok(())
}

#[tauri::command]
pub fn quit(app: AppHandle, window: WebviewWindow) -> Result<(), String> {
    if window.label() != "bootstrap" {
        return Err("quit 仅允许失败页调用".into());
    }
    if let Some(state) = app.try_state::<AppState>() {
        state.shutting_down.store(true, Ordering::SeqCst);
    }
    app.exit(0);
    Ok(())
}
