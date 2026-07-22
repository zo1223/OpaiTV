import * as ReactNative from "react-native";

type TVEventHandler = (event: any) => void;
type ReactNativeWithTVEventHandler = typeof ReactNative & {
  useTVEventHandler?: (handler: TVEventHandler) => void;
};

export const useSafeTVEventHandler = (handler: TVEventHandler) => {
  const nativeUseTVEventHandler = (ReactNative as ReactNativeWithTVEventHandler).useTVEventHandler;

  if (typeof nativeUseTVEventHandler === "function") {
    nativeUseTVEventHandler(handler);
  }
};
