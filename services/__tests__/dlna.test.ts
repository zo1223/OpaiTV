import {
  AV_TRANSPORT_SERVICE,
  MEDIA_RENDERER_DEVICE,
  SSDP_ADDRESS,
  SSDP_PORT,
  SsdpTransport,
  buildMSearch,
  discoverDevices,
  parseSsdpResponse,
} from "../dlna/ssdp";
import { parseDeviceDescription, resolveUrl } from "../dlna/description";
import {
  DlnaError,
  formatDlnaTime,
  getPositionInfo,
  getTransportInfo,
  isCastableUrl,
  parseDlnaTime,
  pause,
  play,
  seek,
  setAVTransportURI,
  stop,
} from "../dlna/avTransport";

jest.mock("@/utils/Logger", () => {
  const tagged = { info: jest.fn(), warn: jest.fn(), error: jest.fn(), debug: jest.fn() };
  return { __esModule: true, default: { withTag: () => tagged } };
});

const TV_LOCATION = "http://192.168.1.50:49152/description.xml";
const TV_CONTROL = "http://192.168.1.50:49152/upnp/control/AVTransport1";

const descriptionXml = (name = "客厅电视 &amp; 盒子", udn = "uuid:fake-tv-1") => `<?xml version="1.0"?>
<root xmlns="urn:schemas-upnp-org:device-1-0">
  <device>
    <friendlyName>${name}</friendlyName>
    <modelName>FakeTV 55</modelName>
    <UDN>${udn}</UDN>
    <serviceList>
      <service>
        <serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>
        <controlURL>/upnp/control/RenderingControl1</controlURL>
      </service>
      <service>
        <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
        <controlURL>/upnp/control/AVTransport1</controlURL>
      </service>
    </serviceList>
  </device>
</root>`;

const ssdpResponse = (location: string, st: string, usn = "uuid:fake-tv-1") =>
  ["HTTP/1.1 200 OK", "CACHE-CONTROL: max-age=1800", `Location: ${location}`, `st: ${st}`, `USN: ${usn}::${st}`, "", ""].join(
    "\r\n"
  );

const textResponse = (body: string, status = 200) =>
  ({ ok: status >= 200 && status < 300, status, text: async () => body }) as unknown as Response;

/** 假 UDP 传输层:记录发出的报文;每收到一条 M-SEARCH 就把 respond 给出的报文回给监听者。 */
const createFakeTransport = (respond: (message: string) => string[] = () => []) => {
  const sent: { message: string; port: number; address: string }[] = [];
  let listener: ((message: string) => void) | null = null;
  const transport: SsdpTransport & { sent: typeof sent; closed: boolean } = {
    sent,
    closed: false,
    send(message, port, address) {
      sent.push({ message, port, address });
      for (const reply of respond(message)) {
        Promise.resolve().then(() => listener?.(reply));
      }
    },
    onMessage(callback) {
      listener = callback;
    },
    close() {
      transport.closed = true;
    },
  };
  return transport;
};

describe("SSDP", () => {
  it("builds an M-SEARCH for the given search target", () => {
    const message = buildMSearch(AV_TRANSPORT_SERVICE);
    expect(message.startsWith("M-SEARCH * HTTP/1.1\r\n")).toBe(true);
    expect(message).toContain("HOST: 239.255.255.250:1900\r\n");
    expect(message).toContain('MAN: "ssdp:discover"\r\n');
    expect(message).toContain("MX: 2\r\n");
    expect(message).toContain(`ST: ${AV_TRANSPORT_SERVICE}\r\n`);
    expect(message.endsWith("\r\n\r\n")).toBe(true);
  });

  it("parses response headers case-insensitively", () => {
    expect(parseSsdpResponse(ssdpResponse(TV_LOCATION, AV_TRANSPORT_SERVICE))).toEqual({
      location: TV_LOCATION,
      usn: `uuid:fake-tv-1::${AV_TRANSPORT_SERVICE}`,
      st: AV_TRANSPORT_SERVICE,
    });
  });

  it("ignores messages that are not usable discovery responses", () => {
    expect(parseSsdpResponse("")).toBeNull();
    expect(parseSsdpResponse("garbage")).toBeNull();
    expect(parseSsdpResponse("HTTP/1.1 404 Not Found\r\nLOCATION: " + TV_LOCATION + "\r\n\r\n")).toBeNull();
    expect(parseSsdpResponse("HTTP/1.1 200 OK\r\nST: x\r\n\r\n")).toBeNull();
    expect(parseSsdpResponse("HTTP/1.1 200 OK\r\nLOCATION: ftp://192.168.1.50/x\r\n\r\n")).toBeNull();
    expect(parseSsdpResponse("M-SEARCH * HTTP/1.1\r\nLOCATION: " + TV_LOCATION + "\r\n\r\n")).toBeNull();
    expect(
      parseSsdpResponse("NOTIFY * HTTP/1.1\r\nNTS: ssdp:byebye\r\nLOCATION: " + TV_LOCATION + "\r\n\r\n")
    ).toBeNull();
  });

  it("discovers a fake TV: M-SEARCH → response → description → device with control URL", async () => {
    const transport = createFakeTransport((message) => {
      const st = /\r\nST: (.+)\r\n/.exec(message)?.[1] ?? "";
      return [ssdpResponse(TV_LOCATION, st)];
    });
    const fetchImpl = jest.fn(async (url: string) => textResponse(url === TV_LOCATION ? descriptionXml() : "", 200));
    const onDevice = jest.fn();

    const devices = await discoverDevices({
      transport,
      fetchImpl: fetchImpl as unknown as typeof fetch,
      timeoutMs: 40,
      onDevice,
    });

    // 两个 ST 各发一条,都发往多播地址。
    expect(transport.sent.map((item) => /\r\nST: (.+)\r\n/.exec(item.message)?.[1])).toEqual([
      AV_TRANSPORT_SERVICE,
      MEDIA_RENDERER_DEVICE,
    ]);
    expect(transport.sent.every((item) => item.port === SSDP_PORT && item.address === SSDP_ADDRESS)).toBe(true);

    // 设备对两个 ST 各回了一次,只算一台,描述只取一次。
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect(devices).toEqual([
      {
        id: "uuid:fake-tv-1",
        name: "客厅电视 & 盒子",
        host: "192.168.1.50",
        modelName: "FakeTV 55",
        location: TV_LOCATION,
        controlUrl: TV_CONTROL,
        serviceType: AV_TRANSPORT_SERVICE,
      },
    ]);
    expect(onDevice).toHaveBeenCalledTimes(1);
    expect(transport.closed).toBe(true);
  });

  it("dedupes the same device announced from two locations by its UDN", async () => {
    const other = "http://192.168.1.50:49153/description.xml";
    const transport = createFakeTransport((message) =>
      message.includes(AV_TRANSPORT_SERVICE)
        ? [ssdpResponse(TV_LOCATION, AV_TRANSPORT_SERVICE), ssdpResponse(other, AV_TRANSPORT_SERVICE)]
        : []
    );
    const fetchImpl = jest.fn(async () => textResponse(descriptionXml()));

    const devices = await discoverDevices({ transport, fetchImpl: fetchImpl as unknown as typeof fetch, timeoutMs: 40 });

    expect(fetchImpl).toHaveBeenCalledTimes(2);
    expect(devices).toHaveLength(1);
  });

  it("ignores bad responses, failed descriptions and devices without AVTransport", async () => {
    const broken = "http://192.168.1.60:8080/desc.xml";
    const failing = "http://192.168.1.61:8080/desc.xml";
    const printer = "http://192.168.1.62:8080/desc.xml";
    const transport = createFakeTransport((message) =>
      message.includes(AV_TRANSPORT_SERVICE)
        ? [
            "not an ssdp message",
            ssdpResponse(broken, AV_TRANSPORT_SERVICE, "uuid:broken"),
            ssdpResponse(failing, AV_TRANSPORT_SERVICE, "uuid:failing"),
            ssdpResponse(printer, AV_TRANSPORT_SERVICE, "uuid:printer"),
            ssdpResponse(TV_LOCATION, AV_TRANSPORT_SERVICE),
          ]
        : []
    );
    const fetchImpl = jest.fn(async (url: string) => {
      if (url === broken) return textResponse("oops", 500);
      if (url === failing) throw new Error("ECONNREFUSED");
      if (url === printer) {
        return textResponse(
          "<root><device><friendlyName>打印机</friendlyName><serviceList><service><serviceType>urn:schemas-upnp-org:service:PrintBasic:1</serviceType><controlURL>/print</controlURL></service></serviceList></device></root>"
        );
      }
      return textResponse(descriptionXml());
    });

    const devices = await discoverDevices({ transport, fetchImpl: fetchImpl as unknown as typeof fetch, timeoutMs: 40 });

    expect(devices.map((device) => device.id)).toEqual(["uuid:fake-tv-1"]);
  });

  it("returns the devices found so far when the timeout elapses", async () => {
    const slow = "http://192.168.1.70:8080/desc.xml";
    const transport = createFakeTransport((message) =>
      message.includes(AV_TRANSPORT_SERVICE)
        ? [ssdpResponse(TV_LOCATION, AV_TRANSPORT_SERVICE), ssdpResponse(slow, AV_TRANSPORT_SERVICE, "uuid:slow")]
        : []
    );
    const fetchImpl = jest.fn((url: string) =>
      url === slow ? new Promise<Response>(() => {}) : Promise.resolve(textResponse(descriptionXml()))
    );

    const startedAt = Date.now();
    const devices = await discoverDevices({
      transport,
      fetchImpl: fetchImpl as unknown as typeof fetch,
      timeoutMs: 50,
      descriptionTimeoutMs: 10_000,
    });

    expect(devices.map((device) => device.id)).toEqual(["uuid:fake-tv-1"]);
    expect(Date.now() - startedAt).toBeLessThan(2_000);
    expect(transport.closed).toBe(true);
  });

  it("returns an empty list (no throw) when sending fails and nothing answers", async () => {
    const transport = createFakeTransport();
    transport.send = () => {
      throw new Error("ENETUNREACH");
    };
    await expect(discoverDevices({ transport, fetchImpl: jest.fn() as unknown as typeof fetch, timeoutMs: 20 })).resolves.toEqual([]);
    expect(transport.closed).toBe(true);
  });
});

describe("device description", () => {
  it("resolves control URLs: absolute, root-relative and directory-relative", () => {
    expect(resolveUrl("http://192.168.1.9:1/c", TV_LOCATION)).toBe("http://192.168.1.9:1/c");
    expect(resolveUrl("/ctl/AVT", "http://192.168.1.50:49152/dev/desc.xml")).toBe("http://192.168.1.50:49152/ctl/AVT");
    expect(resolveUrl("ctl/AVT", "http://192.168.1.50:49152/dev/desc.xml")).toBe("http://192.168.1.50:49152/dev/ctl/AVT");
    expect(resolveUrl("ctl/AVT", "http://192.168.1.50:49152")).toBe("http://192.168.1.50:49152/ctl/AVT");
    expect(resolveUrl("", TV_LOCATION)).toBeNull();
  });

  it("prefers URLBase and keeps the declared AVTransport version", () => {
    const xml = `<root><URLBase>http://192.168.1.80:7676</URLBase><device><friendlyName>卧室电视</friendlyName>
      <serviceList><service><serviceType>urn:schemas-upnp-org:service:AVTransport:2</serviceType>
      <controlURL>smp_22_</controlURL></service></serviceList></device></root>`;
    const device = parseDeviceDescription(xml, "http://192.168.1.80:9197/dmr");
    expect(device).toMatchObject({
      id: "uuid:192.168.1.80:9197",
      name: "卧室电视",
      controlUrl: "http://192.168.1.80:7676/smp_22_",
      serviceType: "urn:schemas-upnp-org:service:AVTransport:2",
    });
  });

  it("returns null without an AVTransport service or with unusable input", () => {
    expect(parseDeviceDescription("<root><device><friendlyName>x</friendlyName></device></root>", TV_LOCATION)).toBeNull();
    expect(parseDeviceDescription("", TV_LOCATION)).toBeNull();
    expect(parseDeviceDescription(descriptionXml(), "not a url")).toBeNull();
  });
});

describe("AVTransport SOAP", () => {
  const target = { controlUrl: TV_CONTROL, serviceType: AV_TRANSPORT_SERVICE };
  const okBody = (action: string, inner = "") =>
    `<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:${action}Response xmlns:u="${AV_TRANSPORT_SERVICE}">${inner}</u:${action}Response></s:Body></s:Envelope>`;

  const createFetch = (reply: (action: string) => Response = (action) => textResponse(okBody(action))) => {
    const calls: { url: string; action: string; headers: Record<string, string>; body: string; method: string }[] = [];
    const fetchImpl = jest.fn(async (url: string, init: RequestInit) => {
      const headers = init.headers as Record<string, string>;
      const action = /#(\w+)"$/.exec(headers.SOAPACTION)?.[1] ?? "";
      calls.push({ url, action, headers, body: String(init.body), method: String(init.method) });
      return reply(action);
    });
    return { calls, options: { fetchImpl: fetchImpl as unknown as typeof fetch } };
  };

  it("sends Play with the SOAPACTION header and an envelope in the service namespace", async () => {
    const { calls, options } = createFetch();
    await play(target, options);

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toBe(TV_CONTROL);
    expect(calls[0].method).toBe("POST");
    expect(calls[0].headers.SOAPACTION).toBe(`"${AV_TRANSPORT_SERVICE}#Play"`);
    expect(calls[0].headers["Content-Type"]).toBe('text/xml; charset="utf-8"');
    expect(calls[0].body).toContain(`<u:Play xmlns:u="${AV_TRANSPORT_SERVICE}">`);
    expect(calls[0].body).toContain("<InstanceID>0</InstanceID><Speed>1</Speed>");
  });

  it("escapes the media URL and title in SetAVTransportURI", async () => {
    const { calls, options } = createFetch();
    const url = "http://192.168.1.2/v/index.m3u8?token=a&sig=<b>";
    await setAVTransportURI(target, url, 'Tom & "Jerry" <1>', options);

    const body = calls[0].body;
    expect(calls[0].action).toBe("SetAVTransportURI");
    expect(body).toContain("<CurrentURI>http://192.168.1.2/v/index.m3u8?token=a&amp;sig=&lt;b&gt;</CurrentURI>");
    expect(body).not.toContain("token=a&sig");
    // 元数据是放在文本节点里的一段 XML,整体再转义一次。
    expect(body).toContain("<CurrentURIMetaData>&lt;DIDL-Lite ");
    expect(body).toContain("&lt;dc:title&gt;Tom &amp;amp; &amp;quot;Jerry&amp;quot; &amp;lt;1&amp;gt;&lt;/dc:title&gt;");
    expect(body).toContain("object.item.videoItem");
    expect(body).toContain("token=a&amp;amp;sig=&amp;lt;b&amp;gt;&lt;/res&gt;");
  });

  it("sends Pause, Stop and Seek (REL_TIME, HH:MM:SS)", async () => {
    const { calls, options } = createFetch();
    await pause(target, options);
    await seek(target, 3_725_900, options);
    await stop(target, options);

    expect(calls.map((call) => call.action)).toEqual(["Pause", "Seek", "Stop"]);
    expect(calls[1].body).toContain("<Unit>REL_TIME</Unit><Target>01:02:05</Target>");
    expect(calls[2].body).toContain(`<u:Stop xmlns:u="${AV_TRANSPORT_SERVICE}"><InstanceID>0</InstanceID></u:Stop>`);
  });

  it("parses GetPositionInfo and GetTransportInfo", async () => {
    const { options } = createFetch((action) =>
      textResponse(
        action === "GetPositionInfo"
          ? okBody(action, "<Track>1</Track><TrackDuration>0:45:10</TrackDuration><TrackURI>http://192.168.1.2/a.m3u8?x=1&amp;y=2</TrackURI><RelTime>00:12:34.500</RelTime>")
          : okBody(action, "<CurrentTransportState>paused_playback</CurrentTransportState><CurrentTransportStatus>OK</CurrentTransportStatus>")
      )
    );

    await expect(getPositionInfo(target, options)).resolves.toEqual({
      positionMillis: 754_500,
      durationMillis: 2_710_000,
      trackUri: "http://192.168.1.2/a.m3u8?x=1&y=2",
    });
    await expect(getTransportInfo(target, options)).resolves.toEqual({ state: "PAUSED_PLAYBACK", status: "OK" });
  });

  it("treats NOT_IMPLEMENTED and malformed times as zero", () => {
    expect(parseDlnaTime("NOT_IMPLEMENTED")).toBe(0);
    expect(parseDlnaTime("")).toBe(0);
    expect(parseDlnaTime("1:02:03")).toBe(3_723_000);
    expect(formatDlnaTime(59_999)).toBe("00:00:59");
    expect(formatDlnaTime(-5)).toBe("00:00:00");
  });

  it("turns a SOAP Fault into a DlnaError with the UPnP error code", async () => {
    const fault = `<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>701</errorCode><errorDescription>Transition not available</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>`;
    const { options } = createFetch(() => textResponse(fault, 500));

    const error = await seek(target, 1000, options).catch((e) => e);
    expect(error).toBeInstanceOf(DlnaError);
    expect(error.action).toBe("Seek");
    expect(error.status).toBe(500);
    expect(error.upnpCode).toBe("701");
    expect(error.message).toContain("Transition not available");
  });

  it("reports plain HTTP errors and network failures as DlnaError", async () => {
    const http = createFetch(() => textResponse("", 404));
    await expect(play(target, http.options)).rejects.toMatchObject({ name: "DlnaError", status: 404 });

    const network = { fetchImpl: jest.fn(async () => { throw new Error("ECONNRESET"); }) as unknown as typeof fetch };
    await expect(play(target, network)).rejects.toMatchObject({ name: "DlnaError", action: "Play" });
  });

  it("only allows http(s) addresses a TV can reach", () => {
    expect(isCastableUrl("https://media.example/a/index.m3u8")).toBe(true);
    expect(isCastableUrl("http://192.168.1.2:8080/a.mp4")).toBe(true);
    expect(isCastableUrl("file:///storage/emulated/0/a.mp4")).toBe(false);
    expect(isCastableUrl("/data/user/0/a.mp4")).toBe(false);
    expect(isCastableUrl("http://127.0.0.1:9978/proxy.m3u8")).toBe(false);
    expect(isCastableUrl("http://localhost:9978/proxy.m3u8")).toBe(false);
    expect(isCastableUrl("")).toBe(false);
  });
});
