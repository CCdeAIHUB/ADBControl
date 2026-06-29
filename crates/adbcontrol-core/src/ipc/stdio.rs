use std::io::{BufRead, Write};

use crate::{error::AppError, protocol::CoreService, AdbRunner};

pub fn serve_json_lines<R, W, A>(
    service: &CoreService<A>,
    reader: R,
    mut writer: W,
) -> Result<(), AppError>
where
    R: BufRead,
    W: Write,
    A: AdbRunner,
{
    for line in reader.lines() {
        let line = line.map_err(|error| {
            AppError::new(
                "IPC_READ_FAILED",
                "Failed to read IPC request line.",
                "ipc.stdio",
                true,
            )
            .with_cause(error)
        })?;

        let response = service.handle_json_line(&line);
        writer.write_all(response.as_bytes()).map_err(|error| {
            AppError::new(
                "IPC_WRITE_FAILED",
                "Failed to write IPC response line.",
                "ipc.stdio",
                true,
            )
            .with_cause(error)
        })?;
        writer.write_all(b"\n").map_err(|error| {
            AppError::new(
                "IPC_WRITE_FAILED",
                "Failed to write IPC response newline.",
                "ipc.stdio",
                true,
            )
            .with_cause(error)
        })?;
        writer.flush().map_err(|error| {
            AppError::new(
                "IPC_FLUSH_FAILED",
                "Failed to flush IPC response.",
                "ipc.stdio",
                true,
            )
            .with_cause(error)
        })?;
    }

    Ok(())
}
