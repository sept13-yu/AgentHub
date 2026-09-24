use crate::AppState;
use tauri::menu::{Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{AppHandle, Manager};

// 编译期嵌入；Cargo.toml 需 tauri feature "image-png" 才有 Image::from_bytes
const TRAY_ICON_PNG: &[u8] = include_bytes!("../../../AgentHub/wwwroot/icon.png");

pub fn setup_tray(app: &AppHandle) -> tauri::Result<()> {
    let show = MenuItem::with_id(app, "show", "显示 AgentHub", true, None::<&str>)?;
    let sync = MenuItem::with_id(app, "sync", "立即同步", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?;
    let sep = PredefinedMenuItem::separator(app)?;
    let menu = Menu::with_items(app, &[&show, &sync, &sep, &quit])?;

    let icon = tauri::image::Image::from_bytes(TRAY_ICON_PNG)
        .expect("embedded tray icon png must decode");

    TrayIconBuilder::with_id("main-tray")
        .icon(icon)
        .icon_as_template(false)
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id.as_ref() {
            "show" => {
                if let Some(win) = app.get_webview_window("main") {
                    let _ = win.show();
                    let _ = win.set_focus();
                }
            }
            "sync" => {
                // 先取出 token/port 再开线程，避免 MutexGuard 借用跨线程
                let snapshot = {
                    let state = app.state::<AppState>();
                    state.backend.lock().ok().and_then(|guard| {
                        guard.as_ref().map(|b| (b.token.clone(), b.port))
                    })
                };
                if let Some((token, port)) = snapshot {
                    std::thread::spawn(move || {
                        let url = format!("http://127.0.0.1:{port}/api/usage/scan");
                        let _ = ureq::post(&url)
                            .set("X-AgentHub-Token", &token)
                            .set("Host", "127.0.0.1:18780")
                            .timeout(std::time::Duration::from_secs(30))
                            .call();
                    });
                }
            }
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let tauri::tray::TrayIconEvent::DoubleClick { .. } = event {
                let app = tray.app_handle();
                if let Some(win) = app.get_webview_window("main") {
                    let _ = win.show();
                    let _ = win.set_focus();
                }
            }
        })
        .build(app)?;
    Ok(())
}
