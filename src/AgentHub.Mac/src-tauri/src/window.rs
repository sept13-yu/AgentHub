use tauri::{AppHandle, Manager, Theme, WebviewUrl, WebviewWindow, WebviewWindowBuilder, WindowEvent};

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

    WebviewWindowBuilder::new(
        app,
        "main",
        WebviewUrl::External("http://127.0.0.1:18780/app/".parse()?),
    )
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
    // 关窗默认隐藏到菜单栏，不销毁 WebView（token 仍有效）；真正退出走菜单/托盘/⌘Q
    .on_window_event(|window, event| {
        if let WindowEvent::CloseRequested { api, .. } = event {
            api.prevent_close();
            let _ = window.hide();
        }
    })
    .build()
}

fn json_str(s: &str) -> String {
    serde_json::to_string(s).unwrap_or_else(|_| "\"\"".into())
}
