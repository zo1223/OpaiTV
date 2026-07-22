import TcpSocket from 'react-native-tcp-socket';
import NetInfo from '@react-native-community/netinfo';
import { Buffer } from 'buffer';
import Logger from '@/utils/Logger';

const logger = Logger.withTag('TCPHttpServer');

const PORT = 12346;

interface HttpRequest {
  method: string;
  url: string;
  headers: { [key: string]: string };
  body: string;
}

interface HttpResponse {
  statusCode: number;
  headers: { [key: string]: string };
  body: string;
}

type RequestHandler = (request: HttpRequest) => HttpResponse | Promise<HttpResponse>;

class TCPHttpServer {
  private server: TcpSocket.Server | null = null;
  private isRunning = false;
  private requestHandler: RequestHandler | null = null;

  constructor() {
    this.server = null;
  }

  private parseHttpRequest(data: string): HttpRequest | null {
    try {
      const lines = data.split('\r\n');
      const requestLine = lines[0].split(' ');
      
      if (requestLine.length < 3) {
        return null;
      }

      const method = requestLine[0];
      const url = requestLine[1];
      const headers: { [key: string]: string } = {};
      
      let bodyStartIndex = -1;
      for (let i = 1; i < lines.length; i++) {
        const line = lines[i];
        if (line === '') {
          bodyStartIndex = i + 1;
          break;
        }
        const colonIndex = line.indexOf(':');
        if (colonIndex > 0) {
          const key = line.substring(0, colonIndex).trim().toLowerCase();
          const value = line.substring(colonIndex + 1).trim();
          headers[key] = value;
        }
      }

      const body = bodyStartIndex > 0 ? lines.slice(bodyStartIndex).join('\r\n') : '';

      return { method, url, headers, body };
    } catch (error) {
      logger.info('[TCPHttpServer] Error parsing HTTP request:', error);
      return null;
    }
  }

  private formatHttpResponse(response: HttpResponse): string {
    const statusTexts: { [key: number]: string } = {
      200: 'OK',
      400: 'Bad Request',
      404: 'Not Found',
      500: 'Internal Server Error'
    };

    const statusText = statusTexts[response.statusCode] || 'Unknown';
    const headers = {
      'Content-Length': new TextEncoder().encode(response.body).length.toString(),
      'Connection': 'close',
      ...response.headers
    };

    let httpResponse = `HTTP/1.1 ${response.statusCode} ${statusText}\r\n`;
    
    for (const [key, value] of Object.entries(headers)) {
      httpResponse += `${key}: ${value}\r\n`;
    }
    
    httpResponse += '\r\n';
    httpResponse += response.body;

    return httpResponse;
  }

  public setRequestHandler(handler: RequestHandler) {
    this.requestHandler = handler;
  }

  public async start(): Promise<string> {
    const netState = await NetInfo.fetch();
    let ipAddress: string | null = null;
    
    if (netState.type === 'wifi' || netState.type === 'ethernet') {
      ipAddress = (netState.details as any)?.ipAddress ?? null;
    }

    if (!ipAddress) {
      throw new Error('无法获取IP地址，请确认设备已连接到WiFi或以太网。');
    }

    if (this.isRunning) {
      logger.debug('[TCPHttpServer] Server is already running.');
      return `http://${ipAddress}:${PORT}`;
    }

    return new Promise((resolve, reject) => {
      try {
        this.server = TcpSocket.createServer((socket: TcpSocket.Socket) => {
          logger.debug('[TCPHttpServer] Client connected');

          // 以字节为单位累积:分包时 Content-Length 才判得准,跨 chunk 的
          // UTF-8 多字节字符也必须等收齐后一次性解码,逐 chunk toString 会乱码
          let buffered: Buffer = Buffer.alloc(0);
          let handled = false;

          socket.on('data', async (data: string | Buffer) => {
            if (handled) return;
            const chunk = typeof data === 'string' ? Buffer.from(data, 'utf8') : data;
            buffered = Buffer.concat([buffered, chunk]);

            const headerEndIndex = buffered.indexOf('\r\n\r\n');
            if (headerEndIndex === -1) return; // 头部未收完

            const headerText = buffered.slice(0, headerEndIndex).toString('utf8');
            const lengthMatch = headerText.match(/^content-length:\s*(\d+)\s*$/im);
            const contentLength = lengthMatch ? parseInt(lengthMatch[1], 10) : 0;

            if (contentLength > 1000000) {
              handled = true;
              socket.write(this.formatHttpResponse({
                statusCode: 400,
                headers: { 'Content-Type': 'text/plain' },
                body: 'Payload Too Large'
              }));
              socket.end();
              return;
            }

            const totalBytesNeeded = headerEndIndex + 4 + contentLength;
            if (buffered.length < totalBytesNeeded) return; // 正文未收完

            handled = true;
            const requestData = buffered.slice(0, totalBytesNeeded).toString('utf8');
            try {
              const request = this.parseHttpRequest(requestData);
              if (request && this.requestHandler) {
                const response = await this.requestHandler(request);
                const httpResponse = this.formatHttpResponse(response);
                socket.write(httpResponse);
              } else {
                // Send 400 Bad Request for malformed requests
                const errorResponse = this.formatHttpResponse({
                  statusCode: 400,
                  headers: { 'Content-Type': 'text/plain' },
                  body: 'Bad Request'
                });
                socket.write(errorResponse);
              }
            } catch (error) {
              logger.info('[TCPHttpServer] Error handling request:', error);
              const errorResponse = this.formatHttpResponse({
                statusCode: 500,
                headers: { 'Content-Type': 'text/plain' },
                body: 'Internal Server Error'
              });
              socket.write(errorResponse);
            }

            socket.end();
          });

          socket.on('error', (error: Error) => {
            logger.info('[TCPHttpServer] Socket error:', error);
          });

          socket.on('close', () => {
            logger.debug('[TCPHttpServer] Client disconnected');
          });
        });

        this.server.listen({ port: PORT, host: '0.0.0.0' }, () => {
          logger.debug(`[TCPHttpServer] Server listening on ${ipAddress}:${PORT}`);
          this.isRunning = true;
          resolve(`http://${ipAddress}:${PORT}`);
        });

        this.server.on('error', (error: Error) => {
          logger.info('[TCPHttpServer] Server error:', error);
          this.isRunning = false;
          reject(error);
        });

      } catch (error) {
        logger.info('[TCPHttpServer] Failed to start server:', error);
        reject(error);
      }
    });
  }

  public stop() {
    if (this.server && this.isRunning) {
      this.server.close();
      this.server = null;
      this.isRunning = false;
      logger.debug('[TCPHttpServer] Server stopped');
    }
  }

  public getIsRunning(): boolean {
    return this.isRunning;
  }
}

export default TCPHttpServer;