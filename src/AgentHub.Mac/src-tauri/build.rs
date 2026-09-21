fn main() {
    tauri_build::try_build(
        tauri_build::Attributes::new().app_manifest(
            tauri_build::AppManifest::new().commands(&["set_theme", "pick_folder", "retry", "quit"]),
        ),
    )
    .expect("tauri build");
}
