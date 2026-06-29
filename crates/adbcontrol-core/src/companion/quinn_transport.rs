use std::{net::SocketAddr, sync::Arc};

use tokio::{io::AsyncWriteExt, sync::Mutex};

use crate::error::AppError;

use super::listener::CompanionQuicListener;

pub struct QuinnCompanionServer<L>
where
    L: CompanionQuicListener + Send + 'static,
{
    endpoint: quinn::Endpoint,
    listener: Arc<Mutex<L>>,
    max_stream_bytes: usize,
}

impl<L> QuinnCompanionServer<L>
where
    L: CompanionQuicListener + Send + 'static,
{
    pub fn bind(
        listen_addr: SocketAddr,
        server_config: quinn::ServerConfig,
        listener: L,
    ) -> Result<Self, AppError> {
        let endpoint = quinn::Endpoint::server(server_config, listen_addr).map_err(|error| {
            AppError::new(
                "COMPANION_QUIC_BIND_FAILED",
                "Core failed to bind pure QUIC listener.",
                "companion.quinn",
                true,
            )
            .with_cause(error)
        })?;

        Ok(Self {
            endpoint,
            listener: Arc::new(Mutex::new(listener)),
            max_stream_bytes: 1024 * 1024,
        })
    }

    pub fn local_addr(&self) -> Result<SocketAddr, AppError> {
        self.endpoint.local_addr().map_err(|error| {
            AppError::new(
                "COMPANION_QUIC_LOCAL_ADDR_FAILED",
                "Core failed to read QUIC listener local address.",
                "companion.quinn",
                true,
            )
            .with_cause(error)
        })
    }

    pub fn with_max_stream_bytes(mut self, max_stream_bytes: usize) -> Self {
        self.max_stream_bytes = max_stream_bytes;
        self
    }

    pub async fn run(self) -> Result<(), AppError> {
        while let Some(incoming) = self.endpoint.accept().await {
            let listener = Arc::clone(&self.listener);
            let max_stream_bytes = self.max_stream_bytes;
            tokio::spawn(async move {
                let _ = handle_connection(incoming, listener, max_stream_bytes).await;
            });
        }
        Ok(())
    }
}

async fn handle_connection<L>(
    incoming: quinn::Incoming,
    listener: Arc<Mutex<L>>,
    max_stream_bytes: usize,
) -> Result<(), AppError>
where
    L: CompanionQuicListener + Send + 'static,
{
    let connection = incoming.await.map_err(|error| {
        AppError::new(
            "COMPANION_QUIC_HANDSHAKE_FAILED",
            "Core failed to complete QUIC handshake.",
            "companion.quinn",
            true,
        )
        .with_cause(error)
    })?;

    loop {
        tokio::select! {
            stream = connection.accept_bi() => {
                let (send, recv) = stream.map_err(|error| {
                    AppError::new(
                        "COMPANION_QUIC_STREAM_ACCEPT_FAILED",
                        "Core failed to accept QUIC bidirectional stream.",
                        "companion.quinn",
                        true,
                    ).with_cause(error)
                })?;
                handle_bi_stream(send, recv, Arc::clone(&listener), max_stream_bytes).await?;
            }
            datagram = connection.read_datagram() => {
                let datagram = datagram.map_err(|error| {
                    AppError::new(
                        "COMPANION_QUIC_DATAGRAM_READ_FAILED",
                        "Core failed to read QUIC datagram.",
                        "companion.quinn",
                        true,
                    ).with_cause(error)
                })?;
                let mut guard = listener.lock().await;
                let _ = guard.accept_media_bytes(&datagram)?;
            }
        }
    }
}

async fn handle_bi_stream<L>(
    mut send: quinn::SendStream,
    mut recv: quinn::RecvStream,
    listener: Arc<Mutex<L>>,
    max_stream_bytes: usize,
) -> Result<(), AppError>
where
    L: CompanionQuicListener + Send + 'static,
{
    let request = recv.read_to_end(max_stream_bytes).await.map_err(|error| {
        AppError::new(
            "COMPANION_QUIC_STREAM_READ_FAILED",
            "Core failed to read QUIC stream payload.",
            "companion.quinn",
            true,
        )
        .with_cause(error)
    })?;

    let response = {
        let mut guard = listener.lock().await;
        guard.accept_control_bytes(&request)?
    };

    send.write_all(&response).await.map_err(|error| {
        AppError::new(
            "COMPANION_QUIC_STREAM_WRITE_FAILED",
            "Core failed to write QUIC stream response.",
            "companion.quinn",
            true,
        )
        .with_cause(error)
    })?;
    send.finish().map_err(|error| {
        AppError::new(
            "COMPANION_QUIC_STREAM_FINISH_FAILED",
            "Core failed to finish QUIC response stream.",
            "companion.quinn",
            true,
        )
        .with_cause(error)
    })?;

    Ok(())
}
