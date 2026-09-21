mod backend;
mod commands;
mod tray;
mod window;

use backend::{Backend, StartError};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use tauri::menu::{Menu, PredefinedMenuItem, Submenu};
use tauri::{Manager, RunEvent, WebviewUrl, WebviewWindowBuilder};

pub struct AppState {
    pub backend: Arc<Mutex<Option<Backend>>>,
    /// 递增代次：重启后端时旧监视线程自行退出；过期 bootstrap 结果直接丢弃
    pub watch_gen: Arc<AtomicU64>,
    pub shutting_down: Arc<AtomicBool>,
    /// 同一时刻只允许一次 bootstrap/重试，避免并发启动互相拆窗
    pub bootstrap_in_flight: Arc<AtomicBool>,
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(win) = app.get_webview_window("main") {
                let _ = win.show();
                let _ = win.set_focus();
            } else if let Some(win) = app.get_webview_window("bootstrap") {
                let _ = win.show();
                let _ = win.set_focus();
            }
        }))
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![
            commands::set_theme,
            commands::pick_folder,
            commands::retry,
            commands::quit
        ])
        .setup(|app| {
            app.manage(AppState {
                backend: Arc::new(Mutex::new(None)),
                watch_gen: Arc::new(AtomicU64::new(0)),
                shutting_down: Arc::new(AtomicBool::new(false)),
                bootstrap_in_flight: Arc::new(AtomicBool::new(false)),
            });
            setup_app_menu(app.handle())?;
            tray::setup_tray(app.handle())?;
            // 握手最长 20 秒，不能阻塞 setup / 事件循环；建窗回到主线程
            let handle = app.handle().clone();
            std::thread::spawn(move || bootstrap(&handle));
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("failed to build tauri app");

    app.run(|app_handle, event| match event {
        // Reopen（Dock 图标点击）仅 macOS
        #[cfg(target_os = "macos")]
        RunEvent::Reopen { .. } => {
            if let Some(win) = app_handle.get_webview_window("main") {
                let _ = win.show();
                let _ = win.set_focus();
            }
        }
        RunEvent::ExitRequested { .. } => {
            if let Some(state) = app_handle.try_state::<AppState>() {
                state.shutting_down.store(true, Ordering::SeqCst);
            }
        }
        RunEvent::Exit => {
            if let Some(state) = app_handle.try_state::<AppState>() {
                state.shutting_down.store(true, Ordering::SeqCst);
                if let Ok(mut guard) = state.backend.lock() {
                    if let Some(mut b) = guard.take() {
                        b.stop();
                    }
                }
            }
        }
        _ => {}
    });
}

/// macOS 应用菜单：保证 ⌘C/⌘V/⌘A/⌘Q 等系统编辑与退出行为。
fn setup_app_menu(app: &tauri::AppHandle) -> tauri::Result<()> {
    let app_menu = Submenu::with_items(
        app,
        "AgentHub",
        true,
        &[
            &PredefinedMenuItem::about(app, None, None)?,
            &PredefinedMenuItem::separator(app)?,
            &PredefinedMenuItem::services(app, None)?,
            &PredefinedMenuItem::separator(app)?,
            &PredefinedMenuItem::hide(app, None)?,
            &PredefinedMenuItem::hide_others(app, None)?,
            &PredefinedMenuItem::separator(app)?,
            &PredefinedMenuItem::quit(app, None)?,
        ],
    )?;
    let edit_menu = Submenu::with_items(
        app,
        "编辑",
        true,
        &[
            &PredefinedMenuItem::undo(app, None)?,
            &PredefinedMenuItem::redo(app, None)?,
            &PredefinedMenuItem::separator(app)?,
            &PredefinedMenuItem::cut(app, None)?,
            &PredefinedMenuItem::copy(app, None)?,
            &PredefinedMenuItem::paste(app, None)?,
            &PredefinedMenuItem::select_all(app, None)?,
        ],
    )?;
    let menu = Menu::with_items(app, &[&app_menu, &edit_menu])?;
    app.set_menu(menu)?;
    Ok(())
}

/// 拉起后端并建主窗。可在任意线程调用：握手在调用线程阻塞，窗口操作派发到主线程。
/// 同一时刻只允许一次；结果按 watch_gen 校验，过期结果回收后丢弃。
pub fn bootstrap(app: &tauri::AppHandle) {
    let gen = {
        let state = app.state::<AppState>();
        if state
            .bootstrap_in_flight
            .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
            .is_err()
        {
            return;
        }
        state.shutting_down.store(false, Ordering::SeqCst);
        let gen = state.watch_gen.fetch_add(1, Ordering::SeqCst) + 1;
        if let Ok(mut guard) = state.backend.lock() {
            if let Some(mut old) = guard.take() {
                old.stop();
            }
        }
        gen
    };

    let result = match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| Backend::spawn(app)))
    {
        Ok(r) => r,
        Err(_) => {
            if let Some(state) = app.try_state::<AppState>() {
                state.bootstrap_in_flight.store(false, Ordering::SeqCst);
            }
            return;
        }
    };
    let app2 = app.clone();
    let _ = app.run_on_main_thread(move || finish_bootstrap(&app2, result, gen));
}

fn finish_bootstrap(app: &tauri::AppHandle, result: Result<Backend, StartError>, gen: u64) {
    // in_flight 保持到本函数结束，避免建窗过程中又被 retry 打断
    struct ClearInFlight<'a>(&'a tauri::AppHandle);
    impl Drop for ClearInFlight<'_> {
        fn drop(&mut self) {
            if let Some(state) = self.0.try_state::<AppState>() {
                state.bootstrap_in_flight.store(false, Ordering::SeqCst);
            }
        }
    }
    let _clear = ClearInFlight(app);

    if let Some(state) = app.try_state::<AppState>() {
        // 过期启动：不建窗、不弹失败，只回收后端，避免销毁更新一轮已成功的主窗
        if state.watch_gen.load(Ordering::SeqCst) != gen {
            if let Ok(mut stale) = result {
                stale.stop();
            }
            return;
        }
        if state.shutting_down.load(Ordering::SeqCst) {
            if let Ok(mut stale) = result {
                stale.stop();
            }
            return;
        }
    }
    match result {
        Ok(backend) => {
            let token = backend.token.clone();
            let theme = backend.fetch_theme().unwrap_or_else(|| "dark".to_string());
            if let Ok(mut guard) = app.state::<AppState>().backend.lock() {
                *guard = Some(backend);
            }
            if let Some(win) = app.get_webview_window("bootstrap") {
                let _ = win.destroy();
            }
            if let Some(win) = app.get_webview_window("main") {
                let _ = win.destroy();
            }
            if let Err(e) = window::create_main(app, &token, &theme) {
                show_bootstrap(app, &format!("创建主窗失败：{e}"), "");
                return;
            }
            spawn_backend_watch(app.clone(), gen);
        }
        Err(err) => {
            let msg = match &err {
                StartError::Spawn(e) => format!("无法启动后端：{e}"),
                StartError::Exited(code, _) => format!(
                    "后端启动失败（退出码 {}）",
                    code.map(|c| c.to_string()).unwrap_or_else(|| "被信号终止".into())
                ),
                StartError::Timeout => "后端启动超时".into(),
                StartError::PidMismatch => "端口上的进程不是 AgentHub 后端，已中止注入".into(),
            };
            let detail = err.stderr_tail().join("\n");
            show_bootstrap(app, &msg, &detail);
        }
    }
}

/// 运行中 Backend 退出 → 销毁主窗（token 失效）并弹失败页。不做自动重启。
/// 用 Child::try_wait 探活并回收；kill(pid, 0) 对僵尸进程仍成功，不能用。
fn spawn_backend_watch(app: tauri::AppHandle, gen: u64) {
    std::thread::spawn(move || loop {
        std::thread::sleep(std::time::Duration::from_secs(2));
        let Some(state) = app.try_state::<AppState>() else { return };
        if state.shutting_down.load(Ordering::SeqCst) || state.watch_gen.load(Ordering::SeqCst) != gen {
            return;
        }
        let exited_pid = {
            let Ok(mut guard) = state.backend.lock() else { return };
            // match 守卫不能调用 &mut 方法（E0596），在分支内判断
            match guard.as_mut() {
                Some(b) => {
                    if b.has_exited() {
                        let pid = b.pid;
                        *guard = None;
                        Some(pid)
                    } else {
                        None
                    }
                }
                None => return,
            }
        };
        let Some(pid) = exited_pid else { continue };
        let app2 = app.clone();
        let _ = app.run_on_main_thread(move || {
            if let Some(state) = app2.try_state::<AppState>() {
                if state.shutting_down.load(Ordering::SeqCst) {
                    return;
                }
                if state.watch_gen.load(Ordering::SeqCst) != gen {
                    return;
                }
            }
            show_bootstrap(&app2, "本地服务已退出，请重试", &format!("pid={pid} 已退出"));
        });
        return;
    });
}

fn show_bootstrap(app: &tauri::AppHandle, msg: &str, detail: &str) {
    if let Some(win) = app.get_webview_window("main") {
        let _ = win.destroy();
    }
    if let Some(win) = app.get_webview_window("bootstrap") {
        let _ = win.destroy();
    }
    let query = format!(
        "?msg={}&detail={}",
        urlencoding_lite(msg),
        urlencoding_lite(detail)
    );
    let url = WebviewUrl::App(format!("index.html{query}").into());
    let _ = WebviewWindowBuilder::new(app, "bootstrap", url)
        .title("AgentHub")
        .inner_size(480.0, 360.0)
        .resizable(false)
        .build();
}

fn urlencoding_lite(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(b as char)
            }
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}
