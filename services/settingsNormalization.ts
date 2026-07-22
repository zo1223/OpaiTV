export const normalizeApiBaseUrl = (url: string) => {
  let processedApiBaseUrl = url.trim();

  if (!processedApiBaseUrl) {
    return "";
  }

  if (processedApiBaseUrl.endsWith("/")) {
    processedApiBaseUrl = processedApiBaseUrl.slice(0, -1);
  }

  if (!/^https?:\/\//i.test(processedApiBaseUrl)) {
    const hostPart = processedApiBaseUrl.split("/")[0];
    const isIpAddress = /^((\d{1,3}\.){3}\d{1,3})(:\d+)?$/.test(hostPart);
    const hasPort = /:\d+/.test(hostPart);

    if (isIpAddress || hasPort) {
      processedApiBaseUrl = "http://" + processedApiBaseUrl;
    } else {
      processedApiBaseUrl = "https://" + processedApiBaseUrl;
    }
  }

  return processedApiBaseUrl;
};
