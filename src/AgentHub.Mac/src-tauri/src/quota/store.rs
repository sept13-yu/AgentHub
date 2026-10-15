use super::model::Preferences;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64};
use std::sync::Mutex;
use tauri::{AppHandle, Manager};

pub struct State {
    pub prefs: Mutex<Preferences>,
    pub generation: AtomicU64,
    pub dragging: AtomicBool,
    pub pointer_mode_dirty: AtomicBool,
    path: PathBuf,
    corrupt: AtomicBool,
}

pub fn initialize(app: &AppHandle) -> Result<(), String> {
    let dir = app.path().app_config_dir().map_err(|e| e.to_string())?;
    let path = dir.join("desktop-quota.json");
    let (prefs, corrupt) = match std::fs::read(&path) {
        Ok(bytes) => match serde_json::from_slice::<Preferences>(&bytes) {
            Ok(mut prefs) => {
                prefs.sanitize();
                (prefs, false)
            }
            Err(e) => {
                eprintln!("[quota] invalid preferences at {}: {e}", path.display());
                (Preferences::default(), true)
            }
        },
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => (Preferences::default(), false),
        Err(e) => {
            eprintln!("[quota] cannot read preferences at {}: {e}", path.display());
            (Preferences::default(), true)
        }
    };
    app.manage(State {
        prefs: Mutex::new(prefs),
        generation: AtomicU64::new(0),
        dragging: AtomicBool::new(false),
        pointer_mode_dirty: AtomicBool::new(false),
        path,
        corrupt: AtomicBool::new(corrupt),
    });
    Ok(())
}

pub fn snapshot(app: &AppHandle) -> Preferences {
    app.state::<State>()
        .prefs
        .lock()
        .map(|p| p.clone())
        .unwrap_or_default()
}

pub fn update(app: &AppHandle, edit: impl FnOnce(&mut Preferences)) -> Result<Preferences, String> {
    let state = app.state::<State>();
    let mut prefs = state
        .prefs
        .lock()
        .map_err(|_| "quota state unavailable".to_string())?;
    let mut candidate = prefs.clone();
    edit(&mut candidate);
    candidate.sanitize();
    persist(&state, &candidate)?;
    *prefs = candidate.clone();
    Ok(candidate)
}

pub fn persist_current(app: &AppHandle) -> Result<(), String> {
    let state = app.state::<State>();
    let prefs = state
        .prefs
        .lock()
        .map_err(|_| "quota state unavailable".to_string())?;
    persist(&state, &prefs)
}

fn persist(state: &State, prefs: &Preferences) -> Result<(), String> {
    let parent = state
        .path
        .parent()
        .ok_or_else(|| "invalid preferences path".to_string())?;
    std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    if state
        .corrupt
        .swap(false, std::sync::atomic::Ordering::SeqCst)
        && state.path.exists()
    {
        let backup = state.path.with_extension("json.corrupt");
        if let Err(e) = std::fs::copy(&state.path, backup) {
            state
                .corrupt
                .store(true, std::sync::atomic::Ordering::SeqCst);
            return Err(format!("cannot preserve corrupt preferences: {e}"));
        }
    }
    let temp = state
        .path
        .with_extension(format!("json.tmp-{}", std::process::id()));
    let bytes = serde_json::to_vec_pretty(prefs).map_err(|e| e.to_string())?;
    let mut file = std::fs::File::create(&temp).map_err(|e| e.to_string())?;
    use std::io::Write;
    file.write_all(&bytes).map_err(|e| e.to_string())?;
    file.sync_all().map_err(|e| e.to_string())?;
    std::fs::rename(&temp, &state.path).map_err(|e| e.to_string())
}
