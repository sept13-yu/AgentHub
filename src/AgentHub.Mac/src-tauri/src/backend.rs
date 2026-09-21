use rand::RngCore;
use serde::Deserialize;
use std::collections::VecDeque;
use std::io::{BufRead, BufReader};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Manager};

pub enum StartError {
    Spawn(std::io::Error),
    Exited(Option<i32>, Vec<String>),
    Timeout,
    PidMismatch,
}

impl StartError {
    pub fn stderr_tail(&self) -> Vec<String> {
        match self {
            StartError::Exited(_, tail) => tail.clone(),
            _ => vec![],
        }
    }
}

pub struct Backend {
    child: Child,
    pub token: String,
    pub pid: u32,
    pub port: u16,
    stderr_tail: Arc<Mutex<VecDeque<String>>>,
}

#[derive(Deserialize)]
struct ReadyLine {
    port: u16,
    pid: u32,
    #[allow(dead_code)]
    version: String,
}

#[derive(Deserialize)]
struct Health {
    pid: u32,
}

fn random_hex_32_bytes() -> String {
    let mut bytes = [0u8; 32];
    rand::rngs::OsRng.fill_bytes(&mut bytes);
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

impl Backend {
    /// 子进程是否已退出（同时回收，避免僵尸）。
    /// 只有 try_wait 明确返回 Some 才算退出；Err 不当作已退出，避免误拆后端。
    pub fn has_exited(&mut self) -> bool {
        matches!(self.child.try_wait(), Ok(Some(_)))
    }

    pub fn spawn(app: &AppHandle) -> Result<Backend, StartError> {
        let dir = std::env::var_os("AGENTHUB_BACKEND_DIR")
            .map(PathBuf::from)
            .unwrap_or_else(|| {
                app.path()
                    .resource_dir()
                    .map(|p| p.join("backend"))
                    .unwrap_or_else(|_| PathBuf::from("backend"))
            });
        let exe = dir.join("AgentHub.Backend");
        let token = random_hex_32_bytes();
        let mut child = Command::new(&exe)
            .arg("--parent-pid")
            .arg(std::process::id().to_string())
            .env("AGENTHUB_WRITE_TOKEN", &token)
            .env_remove("DOTNET_ROOT")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(StartError::Spawn)?;

        let stderr_tail: Arc<Mutex<VecDeque<String>>> = Arc::new(Mutex::new(VecDeque::new()));
        if let Some(stderr) = child.stderr.take() {
            let tail = Arc::clone(&stderr_tail);
            std::thread::spawn(move || {
                let reader = BufReader::new(stderr);
                for line in reader.lines().map_while(Result::ok) {
                    eprintln!("[backend] {line}");
                    let mut g = tail.lock().unwrap();
                    if g.len() >= 50 {
                        g.pop_front();
                    }
                    g.push_back(line);
                }
            });
        }

        // stdout 读取放独立线程：read_line 会阻塞；EOF 用 None 表示，避免空行被误判为退出
        let stdout = child.stdout.take().expect("stdout piped");
        let (tx, rx) = std::sync::mpsc::channel::<Option<String>>();
        std::thread::spawn(move || {
            let mut reader = BufReader::new(stdout);
            let mut line = String::new();
            loop {
                line.clear();
                match reader.read_line(&mut line) {
                    Ok(0) => {
                        let _ = tx.send(None);
                        break;
                    }
                    Ok(_) => {
                        if tx.send(Some(line.clone())).is_err() {
                            break;
                        }
                    }
                    Err(_) => {
                        let _ = tx.send(None);
                        break;
                    }
                }
            }
        });

        let deadline = Instant::now() + Duration::from_secs(20);
        let mut ready: Option<ReadyLine> = None;
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                let _ = child.kill();
                let _ = child.wait();
                return Err(StartError::Timeout);
            }
            match rx.recv_timeout(remaining) {
                Ok(None) => {
                    let code = child.wait().ok().and_then(|s| s.code());
                    let tail = stderr_tail.lock().unwrap().iter().cloned().collect();
                    return Err(StartError::Exited(code, tail));
                }
                Ok(Some(line)) => {
                    let trimmed = line.trim();
                    if let Some(json) = trimmed.strip_prefix("AGENTHUB_READY ") {
                        if let Ok(v) = serde_json::from_str::<ReadyLine>(json) {
                            ready = Some(v);
                            break;
                        }
                    }
                    if trimmed.starts_with("AGENTHUB_START_FAILED") {
                        let _ = child.kill();
                        let _ = child.wait();
                        let mut tail =
                            stderr_tail.lock().unwrap().iter().cloned().collect::<Vec<_>>();
                        tail.push(trimmed.to_string());
                        return Err(StartError::Exited(Some(3), tail));
                    }
                }
                Err(_) => {
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(StartError::Timeout);
                }
            }
        }

        let ready = ready.expect("ready set");
        let child_pid = child.id();
        if ready.pid != child_pid {
            let _ = child.kill();
            let _ = child.wait();
            return Err(StartError::PidMismatch);
        }

        let health_url = format!("http://127.0.0.1:{}/health", ready.port);
        let health: Health = match ureq::get(&health_url)
            .timeout(Duration::from_secs(3))
            .call()
        {
            Ok(resp) => match resp.into_json() {
                Ok(h) => h,
                Err(_) => {
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(StartError::PidMismatch);
                }
            },
            Err(_) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(StartError::PidMismatch);
            }
        };
        if health.pid != child_pid {
            let _ = child.kill();
            let _ = child.wait();
            return Err(StartError::PidMismatch);
        }

        Ok(Backend {
            child,
            token,
            pid: child_pid,
            port: ready.port,
            stderr_tail,
        })
    }

    pub fn fetch_theme(&self) -> Option<String> {
        let url = format!("http://127.0.0.1:{}/api/settings", self.port);
        let value: serde_json::Value = ureq::get(&url)
            .timeout(Duration::from_secs(3))
            .call()
            .ok()?
            .into_json()
            .ok()?;
        value
            .get("app")?
            .get("theme")?
            .as_str()
            .map(|s| s.to_string())
    }

    /// SIGTERM → 最多等 5 秒 → SIGKILL
    pub fn stop(&mut self) {
        if self.has_exited() {
            return;
        }
        unsafe {
            libc::kill(self.pid as i32, libc::SIGTERM);
        }
        let deadline = Instant::now() + Duration::from_secs(5);
        while Instant::now() < deadline {
            match self.child.try_wait() {
                Ok(Some(_)) => return,
                Ok(None) => std::thread::sleep(Duration::from_millis(100)),
                Err(_) => break,
            }
        }
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}
