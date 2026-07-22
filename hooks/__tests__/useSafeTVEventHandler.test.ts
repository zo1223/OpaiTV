describe("useSafeTVEventHandler", () => {
  afterEach(() => {
    jest.resetModules();
    jest.dontMock("react-native");
  });

  it("does not throw when react-native does not provide useTVEventHandler", () => {
    jest.doMock("react-native", () => ({}));

    const { useSafeTVEventHandler } = require("../useSafeTVEventHandler");

    expect(() => useSafeTVEventHandler(jest.fn())).not.toThrow();
  });

  it("delegates to useTVEventHandler when react-native provides it", () => {
    const nativeUseTVEventHandler = jest.fn();
    jest.doMock("react-native", () => ({
      useTVEventHandler: nativeUseTVEventHandler,
    }));

    const { useSafeTVEventHandler } = require("../useSafeTVEventHandler");
    const handler = jest.fn();

    useSafeTVEventHandler(handler);

    expect(nativeUseTVEventHandler).toHaveBeenCalledWith(handler);
  });
});
