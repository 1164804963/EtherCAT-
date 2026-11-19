#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod bus;
mod esi;
mod live;
mod timing;
#[cfg(windows)]
mod soem;

fn with_bus<R: tauri::Runtime>(builder: tauri::Builder<R>, bus: bus::Bus) -> tauri::Builder<R> {
    builder
        .manage(bus)
        .invoke_handler(tauri::generate_handler![
            bus::list_adapters,
            bus::scan,
            bus::read_status,
            bus::disconnect,
            bus::telemetry,
            bus::control,
            bus::import_esi,
            bus::save_trace
        ])
}

fn main() {
    use std::sync::{Arc, atomic::{AtomicU8, Ordering}};
    let closing = Arc::new(AtomicU8::new(0));
    with_bus(tauri::Builder::default(), bus::Bus::start().expect("failed to start communication worker"))
        .build(tauri::generate_context!())
        .expect("failed to start the EtherCAT monitor")
        .run(move |handle, event| {
            let tauri::RunEvent::ExitRequested { api, .. } = event else { return; };
            if closing.load(Ordering::Acquire) == 2 { return; }
            api.prevent_exit();
            if closing.compare_exchange(0, 1, Ordering::AcqRel, Ordering::Acquire).is_ok() {
                let handle = handle.clone();
                let closing = closing.clone();
                tauri::async_runtime::spawn(async move {
                    let _ = tokio::time::timeout(std::time::Duration::from_millis(1200), bus::shutdown(&handle)).await;
                    closing.store(2, Ordering::Release);
                    handle.exit(0);
                });
            }
        });
}
