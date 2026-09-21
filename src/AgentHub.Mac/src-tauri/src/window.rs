use tauri::{AppHandle, Theme, WebviewUrl, WebviewWindow, WebviewWindowBuilder, WindowEvent};

pub fn create_main(app: &AppHandle, token: &str, theme: &str) -> tauri::Result<WebviewWindow> {
    let script = format!(
        r#"
window.__AGENTHUB_HOST__='tauri';
window.__AGENTHUB_SHELL__=true;
window.__AGENTHUB_TOKEN__={token};
window.__AGENTHUB_THEME__={theme};
(function(){{
  if(location.origin!=='http://127.0.0.1:18780'||!location.pathname.startsWith('/app/'))return;
  var t=window.__AGENTHUB_TOKEN__; if(!t||!window.fetch)return;
  var of=window.fetch;
  window.fetch=function(input,init){{
    var u=new URL(typeof input==='string'?input:input.url,location.href);
    if(u.origin!==location.origin)return of.call(window,input,init);
    init=init||{{}};init.headers=new Headers(init.headers||{{}});
    init.headers.set('X-AgentHub-Token',t);return of.call(window,input,init);}};
  try{{var th=window.__AGENTHUB_THEME__||'dark';
    document.documentElement.setAttribute('data-theme',th);
    localStorage.setItem('agenthub-theme',th);}}catch(e){{}}
}})();"#,
        token = json_str(token),
        theme = json_str(theme)
    );

    // 字面量 URL，parse 失败说明代码写错，直接 panic 而不是错误类型不匹配
    let external = WebviewUrl::External(
        "http://127.0.0.1:18780/app/"
            .parse()
            .expect("backend app url is valid"),
    );

    let win = WebviewWindowBuilder::new(app, "main", external)
        .title("AgentHub")
        .inner_size(1440.0, 900.0)
        .min_inner_size(960.0, 600.0)
        .initialization_script(&script)
        .theme(Some(if theme == "light" {
            Theme::Light
        } else {
            Theme::Dark
        }))
        .on_navigation(|url| {
            if url.scheme() == "http"
                && url.host_str() == Some("127.0.0.1")
                && url.port() == Some(18780)
                && url.path().starts_with("/app/")
            {
                return true;
            }
            if url.scheme() == "http" || url.scheme() == "https" {
                let _ = tauri_plugin_opener::open_url(url.as_str(), None::<&str>);
            }
            false
        })
        .build()?;

    // Tauri 2.11：WebviewWindowBuilder 无 on_window_event，建窗后再挂
    // 关窗默认隐藏到菜单栏，不销毁 WebView（token 仍有效）；真正退出走菜单/托盘/⌘Q
    let win_for_event = win.clone();
    win.on_window_event(move |event| {
        if let WindowEvent::CloseRequested { api, .. } = event {
            api.prevent_close();
            let _ = win_for_event.hide();
        }
    });
    Ok(win)
}

fn json_str(s: &str) -> String {
    serde_json::to_string(s).unwrap_or_else(|_| "\"\"".into())
}
