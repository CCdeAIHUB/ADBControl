import { EventEmitter } from 'node:events';
import net from 'node:net';
import tls from 'node:tls';

const HEADER_BYTES = 4;
const DEFAULT_MAX_FRAME_BYTES = 16 * 1024 * 1024;

export class FramedJsonConnection extends EventEmitter {
  constructor(socket, { maxFrameBytes = DEFAULT_MAX_FRAME_BYTES } = {}) {
    super();
    this.socket = socket;
    this.maxFrameBytes = maxFrameBytes;
    this.buffer = Buffer.alloc(0);
    socket.on('data', (chunk) => this.#onData(chunk));
    socket.on('close', () => this.emit('close'));
    socket.on('error', (error) => this.emit('error', error));
  }

  send(message) {
    const payload = Buffer.from(JSON.stringify(message), 'utf8');
    if (payload.byteLength > this.maxFrameBytes) {
      throw new RangeError(`frame exceeds ${this.maxFrameBytes} bytes`);
    }
    const header = Buffer.alloc(HEADER_BYTES);
    header.writeUInt32BE(payload.byteLength, 0);
    this.socket.write(Buffer.concat([header, payload]));
  }

  close() {
    this.socket.end();
  }

  #onData(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.byteLength >= HEADER_BYTES) {
      const frameLength = this.buffer.readUInt32BE(0);
      if (frameLength > this.maxFrameBytes) {
        this.emit('error', new RangeError(`incoming frame exceeds ${this.maxFrameBytes} bytes`));
        this.socket.destroy();
        return;
      }
      if (this.buffer.byteLength < HEADER_BYTES + frameLength) return;
      const body = this.buffer.subarray(HEADER_BYTES, HEADER_BYTES + frameLength);
      this.buffer = this.buffer.subarray(HEADER_BYTES + frameLength);
      try {
        this.emit('message', JSON.parse(body.toString('utf8')));
      } catch (error) {
        this.emit('error', error);
      }
    }
  }
}

export function createTcpFramedServer({ host = '127.0.0.1', port = 0, tlsOptions = null, onConnection }) {
  const server = tlsOptions
    ? tls.createServer(tlsOptions, (socket) => onConnection(new FramedJsonConnection(socket)))
    : net.createServer((socket) => onConnection(new FramedJsonConnection(socket)));

  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, host, () => {
      server.off('error', reject);
      resolve({
        server,
        address: server.address(),
        close: () => new Promise((done) => server.close(done)),
      });
    });
  });
}

export function connectTcpFramedClient({ host, port, tlsOptions = null }) {
  return new Promise((resolve, reject) => {
    const socket = tlsOptions ? tls.connect({ host, port, ...tlsOptions }) : net.connect({ host, port });
    socket.once('error', reject);
    socket.once(tlsOptions ? 'secureConnect' : 'connect', () => {
      socket.off('error', reject);
      resolve(new FramedJsonConnection(socket));
    });
  });
}
