import { StyleSheet } from "react-native";
import {
  createResponsiveStyles,
  getCommonResponsiveStyles,
  getResponsiveTextSize,
  getResponsiveSpacing,
  ResponsiveStyleCreator,
} from "../ResponsiveStyles";
import { ResponsiveConfig } from "@/hooks/useResponsiveLayout";
import { DeviceUtils } from "../DeviceUtils";

jest.mock("react-native", () => ({
  StyleSheet: {
    create: jest.fn((styles) => styles),
  },
}));

jest.mock("@/hooks/useResponsiveLayout", () => ({
  useResponsiveLayout: jest.fn(),
}));

jest.mock("@/utils/DeviceUtils", () => ({
  DeviceUtils: {
    getMinTouchTargetSize: jest.fn(),
    getOptimalFontSize: jest.fn(),
  },
}));

const mockedStyleSheet = StyleSheet as jest.Mocked<typeof StyleSheet>;
const mockedDeviceUtils = DeviceUtils as jest.Mocked<typeof DeviceUtils>;

describe("ResponsiveStyles", () => {
  const mockConfig: ResponsiveConfig = {
    deviceType: "mobile",
    columns: 3,
    cardWidth: 110,
    cardHeight: 165,
    spacing: 16,
    isPortrait: true,
    screenWidth: 375,
    screenHeight: 812,
  };

  beforeEach(() => {
    jest.clearAllMocks();
    mockedDeviceUtils.getMinTouchTargetSize.mockReturnValue(44);
    mockedDeviceUtils.getOptimalFontSize.mockImplementation((size) => size);
  });

  describe("createResponsiveStyles", () => {
    it("应该创建响应式样式函数", () => {
      const styleCreator: ResponsiveStyleCreator<any> = (config) => ({
        container: {
          padding: config.spacing,
        },
      });

      const responsiveStylesFunc = createResponsiveStyles(styleCreator);
      const styles = responsiveStylesFunc(mockConfig);

      expect(mockedStyleSheet.create).toHaveBeenCalledWith({
        container: {
          padding: 16,
        },
      });
      expect(styles).toEqual({
        container: {
          padding: 16,
        },
      });
    });
  });

  describe("getCommonResponsiveStyles", () => {
    beforeEach(() => {
      mockedDeviceUtils.getOptimalFontSize.mockImplementation((size) => {
        const deviceType = "mobile";
        const scaleFactor = {
          mobile: 1.0,
          tablet: 1.1,
          tv: 1.25,
        }[deviceType];
        return Math.round(size * scaleFactor);
      });
    });

    it("应该为 mobile 设备返回正确的样式", () => {
      const mobileConfig: ResponsiveConfig = {
        ...mockConfig,
        deviceType: "mobile",
        spacing: 16,
      };

      mockedDeviceUtils.getMinTouchTargetSize.mockReturnValue(44);

      const styles = getCommonResponsiveStyles(mobileConfig);

      expect(styles.container).toEqual({
        flex: 1,
        paddingHorizontal: 16,
      });

      expect(styles.safeContainer).toEqual({
        flex: 1,
        paddingHorizontal: 16,
        paddingTop: 20,
      });

      expect(styles.primaryButton).toEqual({
        minHeight: 44,
        paddingHorizontal: 24,
        paddingVertical: 16,
        borderRadius: 8,
        justifyContent: "center",
        alignItems: "center",
      });
    });

    it("应该为 tablet 设备返回正确的样式", () => {
      const tabletConfig: ResponsiveConfig = {
        ...mockConfig,
        deviceType: "tablet",
        spacing: 20,
      };

      mockedDeviceUtils.getMinTouchTargetSize.mockReturnValue(48);

      const styles = getCommonResponsiveStyles(tabletConfig);

      expect(styles.safeContainer.paddingTop).toBe(30);
      expect(styles.primaryButton.borderRadius).toBe(10);
      expect(styles.primaryButton.minHeight).toBe(48);
    });

    it("应该为 tv 设备返回正确的样式", () => {
      const tvConfig: ResponsiveConfig = {
        ...mockConfig,
        deviceType: "tv",
        spacing: 24,
      };

      mockedDeviceUtils.getMinTouchTargetSize.mockReturnValue(60);

      const styles = getCommonResponsiveStyles(tvConfig);

      expect(styles.safeContainer.paddingTop).toBe(40);
      expect(styles.primaryButton.borderRadius).toBe(12);
      expect(styles.primaryButton.minHeight).toBe(60);
    });

    it("应该为 tv 设备不包含阴影样式", () => {
      const tvConfig: ResponsiveConfig = {
        ...mockConfig,
        deviceType: "tv",
        spacing: 24,
      };

      const styles = getCommonResponsiveStyles(tvConfig);

      expect(styles.shadow).toEqual({});
    });

    it("应该为非 tv 设备包含阴影样式", () => {
      const mobileConfig: ResponsiveConfig = {
        ...mockConfig,
        deviceType: "mobile",
        spacing: 16,
      };

      const styles = getCommonResponsiveStyles(mobileConfig);

      expect(styles.shadow).toEqual({
        shadowColor: "#000",
        shadowOffset: {
          width: 0,
          height: 2,
        },
        shadowOpacity: 0.25,
        shadowRadius: 3.84,
        elevation: 5,
      });
    });
  });

  describe("getResponsiveTextSize", () => {
    it("应该为 mobile 设备返回基础大小", () => {
      const result = getResponsiveTextSize(16, "mobile");
      expect(result).toBe(16);
    });

    it("应该为 tablet 设备返回缩放后的大小", () => {
      const result = getResponsiveTextSize(16, "tablet");
      expect(result).toBe(18); // 16 * 1.1 = 17.6, rounded to 18
    });

    it("应该为 tv 设备返回缩放后的大小", () => {
      const result = getResponsiveTextSize(16, "tv");
      expect(result).toBe(20); // 16 * 1.25 = 20
    });

    it("应该为未知设备类型返回基础大小", () => {
      const result = getResponsiveTextSize(16, "unknown");
      expect(result).toBe(16);
    });

    it("应该正确处理小数点", () => {
      const result = getResponsiveTextSize(15, "tablet");
      expect(result).toBe(17); // 15 * 1.1 = 16.5, rounded to 17
    });
  });

  describe("getResponsiveSpacing", () => {
    it("应该为 mobile 设备返回缩放后的间距", () => {
      const result = getResponsiveSpacing(20, "mobile");
      expect(result).toBe(16); // 20 * 0.8 = 16
    });

    it("应该为 tablet 设备返回基础间距", () => {
      const result = getResponsiveSpacing(20, "tablet");
      expect(result).toBe(20);
    });

    it("应该为 tv 设备返回缩放后的间距", () => {
      const result = getResponsiveSpacing(20, "tv");
      expect(result).toBe(30); // 20 * 1.5 = 30
    });

    it("应该为未知设备类型返回基础间距", () => {
      const result = getResponsiveSpacing(20, "unknown");
      expect(result).toBe(20);
    });

    it("应该正确处理小数点", () => {
      const result = getResponsiveSpacing(15, "mobile");
      expect(result).toBe(12); // 15 * 0.8 = 12
    });
  });
});