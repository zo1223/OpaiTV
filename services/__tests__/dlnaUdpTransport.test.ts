import { createUdpTransport } from "../dlna/udpTransport";

// 假的 react-native-udp:接口形状照 4.1.7 的 src/UdpSocket.js(bind 失败时回调带错误并发 error 事件;
// send(msg, offset, length, port, address, callback);message 事件给的是 Buffer)。
const mockSockets: any[] = [];
let mockBindError: Error | null = null;
let mockCreateThrows = false;

jest.mock("react-native-udp", () => ({
  __esModule: true,
  default: {
    createSocket: (options: unknown) => {
      if (mockCreateThrows) throw new TypeError("Cannot read properties of undefined (reading 'createSocket')");
      const handlers: Record<string, ((...args: any[]) => void)[]> = {};
      const socket = {
        options,
        sent: [] as any[],
        closed: false,
        on: (event: string, callback: (...args: any[]) => void) => {
          (handlers[event] = handlers[event] || []).push(callback);
        },
        emit: (event: string, ...args: any[]) => (handlers[event] || []).forEach((callback) => callback(...args)),
        bind: (_port: number, callback: (error?: unknown) => void) => {
          Promise.resolve().then(() => {
            if (mockBindError) {
              callback(mockBindError);
              socket.emit("error", mockBindError);
            } else {
              callback();
            }
          });
        },
        send: (...args: any[]) => {
          socket.sent.push(args.slice(0, 5));
          args[5]?.();
        },
        close: () => {
          socket.closed = true;
        },
      };
      mockSockets.push(socket);
      return socket;
    },
  },
}));

describe("udpTransport (react-native-udp adapter)", () => {
  beforeEach(() => {
    mockSockets.length = 0;
    mockBindError = null;
    mockCreateThrows = false;
  });

  it("returns null when the native module is not linked (createSocket throws)", async () => {
    mockCreateThrows = true;
    await expect(createUdpTransport()).resolves.toBeNull();
  });

  it("returns null and closes the socket when binding fails", async () => {
    mockBindError = new Error("EADDRNOTAVAIL");
    await expect(createUdpTransport()).resolves.toBeNull();
    expect(mockSockets[0].closed).toBe(true);
  });

  it("binds, sends with the dgram signature and decodes incoming buffers", async () => {
    const transport = await createUdpTransport();
    expect(transport).not.toBeNull();
    const socket = mockSockets[0];
    expect(socket.options).toEqual({ type: "udp4" });

    const received: string[] = [];
    transport!.onMessage((message) => received.push(message));
    await transport!.send("M-SEARCH * HTTP/1.1\r\n\r\n", 1900, "239.255.255.250");
    expect(socket.sent).toEqual([["M-SEARCH * HTTP/1.1\r\n\r\n", undefined, undefined, 1900, "239.255.255.250"]]);

    socket.emit("message", Buffer.from("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.50:49152/d.xml\r\n\r\n", "utf8"), {});
    expect(received).toEqual(["HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.50:49152/d.xml\r\n\r\n"]);

    transport!.close();
    expect(socket.closed).toBe(true);
    socket.emit("message", Buffer.from("late", "utf8"), {});
    expect(received).toHaveLength(1);
  });
});
