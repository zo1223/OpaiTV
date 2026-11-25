/* eslint-env node */
// react-native-udp(DLNA 投屏的 SSDP 发现用)的 podspec 只声明了 iOS(s.platform = :ios),不支持 tvOS。
// Apple TV 构建(EXPO_TV=1)时跳过它的 iOS 原生部分,否则 pod install 会因平台不兼容而失败。
// TV 端本来就不显示投屏入口;原生模块缺失时 JS 侧返回"未内置 UDP 模块",不会崩溃。
// Android(含 Android TV)不受影响,照常自动链接。
const isTV = process.env.EXPO_TV === "1";

module.exports = {
  dependencies: isTV ? { "react-native-udp": { platforms: { ios: null } } } : {},
};
