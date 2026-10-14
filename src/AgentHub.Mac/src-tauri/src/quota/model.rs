use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use tauri::PhysicalPosition;

pub const DEFAULT_SIZE: u32 = 360;
pub const MIN_SIZE: u32 = 260;
pub const MAX_SIZE: u32 = 460;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(default)]
pub struct Appearance {
    pub size: u32,
    pub glow: f64,
    pub arc_mode: String,
}

impl Default for Appearance {
    fn default() -> Self {
        Self {
            size: DEFAULT_SIZE,
            glow: 0.12,
            arc_mode: "quota".into(),
        }
    }
}

impl Appearance {
    pub fn sanitize(&mut self) {
        self.size = self.size.clamp(MIN_SIZE, MAX_SIZE);
        if !self.glow.is_finite() {
            self.glow = 0.12;
        }
        self.glow = self.glow.clamp(0.0, 0.6);
        if self.arc_mode != "quota" && self.arc_mode != "full" {
            self.arc_mode = "quota".into();
        }
    }

    pub fn validate_message(size: f64, glow: f64, arc_mode: &str) -> Result<Self, String> {
        if !size.is_finite()
            || size.fract() != 0.0
            || !(MIN_SIZE as f64..=MAX_SIZE as f64).contains(&size)
        {
            return Err("size must be an integer from 260 to 460".into());
        }
        if !glow.is_finite() || !(0.0..=0.6).contains(&glow) {
            return Err("glow must be from 0 to 0.6".into());
        }
        if arc_mode != "quota" && arc_mode != "full" {
            return Err("arcMode must be quota or full".into());
        }
        Ok(Self {
            size: size as u32,
            glow,
            arc_mode: arc_mode.into(),
        })
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(default)]
pub struct Preferences {
    pub visible: bool,
    pub expanded: bool,
    pub window_ids: Option<Vec<String>>,
    pub theme: String,
    pub appearance: Appearance,
    pub x: Option<f64>,
    pub y: Option<f64>,
}

impl Default for Preferences {
    fn default() -> Self {
        Self {
            visible: false,
            expanded: false,
            window_ids: None,
            theme: "dark".into(),
            appearance: Appearance::default(),
            x: None,
            y: None,
        }
    }
}

impl Preferences {
    pub fn sanitize(&mut self) {
        self.appearance.sanitize();
        if self.x.is_some_and(|v| !v.is_finite()) {
            self.x = None;
        }
        if self.y.is_some_and(|v| !v.is_finite()) {
            self.y = None;
        }
        if self.theme != "light" && self.theme != "dark" {
            self.theme = "dark".into();
        }
        if let Some(ids) = self.window_ids.as_mut() {
            let mut seen = HashSet::new();
            ids.retain(|id| valid_window_id(id) && seen.insert(id.clone()));
            ids.truncate(2);
        }
    }
}

const SETTINGS_TUCK: f64 = 0.10;
const SETTINGS_BODY: f64 = 250.0;
const SETTINGS_BOTTOM: f64 = 12.0;

/// 与前端 margin、Windows 命中区同一套：设置板高 250，底边 12，上方按表盘尺寸收回 10%。
pub fn outer_size(size: f64, expanded: bool) -> (f64, f64) {
    let width = if expanded { 360.0_f64.max(size) } else { size };
    let height = if expanded {
        size * (1.0 - SETTINGS_TUCK) + SETTINGS_BODY + SETTINGS_BOTTOM
    } else {
        size
    };
    (width, height)
}

pub fn valid_window_id(id: &str) -> bool {
    !id.trim().is_empty()
        && id.len() <= 128
        && !id.chars().any(|c| c.is_control() || c.is_whitespace())
}

pub fn position_is_visible(
    x: f64,
    y: f64,
    width: f64,
    height: f64,
    monitors: &[(f64, f64, f64, f64)],
) -> bool {
    monitors.iter().any(|(mx, my, mw, mh)| {
        let left = x.max(*mx);
        let top = y.max(*my);
        let right = (x + width).min(*mx + *mw);
        let bottom = (y + height).min(*my + *mh);
        right - left >= 80.0 && bottom - top >= 80.0
    })
}

pub fn hit_test(
    cursor: PhysicalPosition<f64>,
    origin: PhysicalPosition<i32>,
    scale: f64,
    window_width: f64,
    size: f64,
    expanded: bool,
) -> bool {
    if !scale.is_finite() || scale <= 0.0 {
        return false;
    }
    let x = (cursor.x - origin.x as f64) / scale;
    let y = (cursor.y - origin.y as f64) / scale;
    let offset = (window_width - size) / 2.0;
    let vx = (x - offset) * 440.0 / size;
    let vy = y * 440.0 / size;
    let circle = (vx - 205.0).powi(2) + (vy - 211.0).powi(2) <= 174.0_f64.powi(2);
    let crown = (368.0..=410.0).contains(&vx) && (176.0..=251.0).contains(&vy);
    let panel_top = size * (1.0 - SETTINGS_TUCK);
    let panel = expanded
        && (12.0..=window_width - 12.0).contains(&x)
        && (panel_top..=panel_top + SETTINGS_BODY).contains(&y);
    circle || crown || panel
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validates_appearance_bounds_and_modes() {
        assert!(Appearance::validate_message(260.0, 0.0, "quota").is_ok());
        assert!(Appearance::validate_message(460.0, 0.6, "full").is_ok());
        assert!(Appearance::validate_message(259.0, 0.12, "quota").is_err());
        assert!(Appearance::validate_message(360.5, 0.12, "quota").is_err());
        assert!(Appearance::validate_message(360.0, f64::NAN, "quota").is_err());
        assert!(Appearance::validate_message(360.0, 0.12, "other").is_err());
    }

    #[test]
    fn sanitizes_corrupt_preferences_without_unbounded_values() {
        let mut p = Preferences {
            appearance: Appearance {
                size: 999,
                glow: f64::INFINITY,
                arc_mode: "bad".into(),
            },
            theme: "invalid".into(),
            window_ids: Some(vec!["codex:main".into(), "bad id".into()]),
            ..Default::default()
        };
        p.sanitize();
        assert_eq!(p.appearance.size, MAX_SIZE);
        assert_eq!(p.appearance.glow, 0.12);
        assert_eq!(p.appearance.arc_mode, "quota");
        assert_eq!(p.theme, "dark");
        assert_eq!(p.window_ids.unwrap(), vec!["codex:main"]);
    }

    #[test]
    fn hit_test_tracks_dpi_and_expanded_panel() {
        let inside = PhysicalPosition::new(
            100.0 + 205.0 * 360.0 / 440.0 * 2.0,
            50.0 + 211.0 * 360.0 / 440.0 * 2.0,
        );
        assert!(hit_test(
            inside,
            PhysicalPosition::new(100, 50),
            2.0,
            360.0,
            360.0,
            false
        ));
        let crown = PhysicalPosition::new(
            100.0 + 389.0 * 360.0 / 440.0 * 2.0,
            50.0 + 200.0 * 360.0 / 440.0 * 2.0,
        );
        assert!(hit_test(
            crown,
            PhysicalPosition::new(100, 50),
            2.0,
            360.0,
            360.0,
            false
        ));
        let corner = PhysicalPosition::new(100.0 + 15.0 * 2.0, 50.0 + 15.0 * 2.0);
        assert!(!hit_test(
            corner,
            PhysicalPosition::new(100, 50),
            2.0,
            360.0,
            360.0,
            false
        ));
        let tucked = PhysicalPosition::new(100.0 + 180.0 * 2.0, 50.0 + 340.0 * 2.0);
        assert!(hit_test(
            tucked,
            PhysicalPosition::new(100, 50),
            2.0,
            360.0,
            360.0,
            true
        ));
        assert!(!hit_test(
            tucked,
            PhysicalPosition::new(100, 50),
            2.0,
            360.0,
            360.0,
            false
        ));
        let panel = PhysicalPosition::new(100.0 + 30.0 * 2.0, 50.0 + 500.0 * 2.0);
        assert!(hit_test(
            panel,
            PhysicalPosition::new(100, 50),
            2.0,
            360.0,
            360.0,
            true
        ));
        assert!(!hit_test(
            panel,
            PhysicalPosition::new(100, 50),
            2.0,
            360.0,
            360.0,
            false
        ));
        assert!(!hit_test(
            inside,
            PhysicalPosition::new(100, 50),
            0.0,
            360.0,
            360.0,
            false
        ));
    }

    #[test]
    fn saved_position_must_remain_reachable_on_a_connected_monitor() {
        let monitors = [(-1920.0, 0.0, 1920.0, 1080.0), (0.0, 0.0, 1440.0, 900.0)];
        assert!(position_is_visible(-1900.0, 100.0, 360.0, 360.0, &monitors));
        assert!(position_is_visible(1000.0, 700.0, 360.0, 360.0, &monitors));
        assert!(!position_is_visible(2200.0, 100.0, 360.0, 360.0, &monitors));
    }
}
