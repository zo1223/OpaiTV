import { setNativeTextInputSelection } from "../textInputSelection";

describe("setNativeTextInputSelection", () => {
  it("does not throw when the input ref does not support setNativeProps", () => {
    expect(() => {
      setNativeTextInputSelection({}, { start: 3, end: 3 });
    }).not.toThrow();
  });

  it("sets native selection when setNativeProps is available", () => {
    const setNativeProps = jest.fn();

    setNativeTextInputSelection({ setNativeProps }, { start: 2, end: 4 });

    expect(setNativeProps).toHaveBeenCalledWith({ selection: { start: 2, end: 4 } });
  });
});
