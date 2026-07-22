type NativeTextInputSelection = {
  start: number;
  end: number;
};

type NativeTextInputRef = {
  setNativeProps?: (props: { selection: NativeTextInputSelection }) => void;
} | null | undefined;

export const setNativeTextInputSelection = (
  input: NativeTextInputRef,
  selection: NativeTextInputSelection
) => {
  if (typeof input?.setNativeProps === "function") {
    input.setNativeProps({ selection });
  }
};
