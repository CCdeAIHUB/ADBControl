import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { connectTcpFramedClient, createTcpFramedServer } from '../../core/transport/framed-message-transport.js';

test('framed JSON transport sends messages over TCP sockets', async () => {
  let serverConnection;
  const listener = await createTcpFramedServer({
    onConnection(connection) {
      serverConnection = connection;
      connection.on('message', (message) => connection.send({ echo: message }));
    },
  });

  const client = await connectTcpFramedClient({ host: listener.address.address, port: listener.address.port });
  client.send({ hello: 'world' });
  const [reply] = await once(client, 'message');

  assert.deepEqual(reply, { echo: { hello: 'world' } });
  client.close();
  serverConnection.close();
  await listener.close();
});
