use std::io::{self, BufReader};

use adbcontrol_core::{
    ipc::stdio::serve_json_lines, load_embedded_manifest, CoreService, ProcessAdbRunner,
};

fn main() {
    let manifest = match load_embedded_manifest() {
        Ok(manifest) => manifest,
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(1);
        }
    };

    let service = CoreService::new(ProcessAdbRunner, manifest);
    let stdin = io::stdin();
    let stdout = io::stdout();

    if let Err(error) = serve_json_lines(&service, BufReader::new(stdin.lock()), stdout.lock()) {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
