import React, { forwardRef } from 'react';
import { TextInput, View, StyleSheet, ViewStyle, TextStyle } from 'react-native';
import { ThemedText } from '@/components/ThemedText';
import { useResponsiveLayout } from '@/hooks/useResponsiveLayout';
import { DeviceUtils } from '@/utils/DeviceUtils';

interface ResponsiveTextInputProps {
  placeholder?: string;
  value: string;
  onChangeText: (text: string) => void;
  label?: string;
  error?: string;
  secureTextEntry?: boolean;
  keyboardType?: 'default' | 'numeric' | 'email-address' | 'phone-pad';
  multiline?: boolean;
  numberOfLines?: number;
  editable?: boolean;
  style?: ViewStyle;
  inputStyle?: TextStyle;
  onFocus?: () => void;
  onBlur?: () => void;
}

const ResponsiveTextInput = forwardRef<TextInput, ResponsiveTextInputProps>(
  (
    {
      placeholder,
      value,
      onChangeText,
      label,
      error,
      secureTextEntry = false,
      keyboardType = 'default',
      multiline = false,
      numberOfLines = 1,
      editable = true,
      style,
      inputStyle,
      onFocus,
      onBlur,
    },
    ref
  ) => {
    const { deviceType, spacing } = useResponsiveLayout();
    const dynamicStyles = createResponsiveStyles(deviceType, spacing);

    return (
      <View style={[dynamicStyles.container, style]}>
        {label && (
          <ThemedText style={dynamicStyles.label}>{label}</ThemedText>
        )}
        
        <View style={[
          dynamicStyles.inputContainer,
          error ? dynamicStyles.errorContainer : undefined,
          !editable ? dynamicStyles.disabledContainer : undefined,
        ]}>
          <TextInput
            ref={ref}
            style={[dynamicStyles.input, inputStyle]}
            placeholder={placeholder}
            placeholderTextColor="#888"
            value={value}
            onChangeText={onChangeText}
            secureTextEntry={secureTextEntry}
            keyboardType={keyboardType}
            multiline={multiline}
            numberOfLines={multiline ? numberOfLines : 1}
            editable={editable}
            onFocus={onFocus}
            onBlur={onBlur}
          />
        </View>

        {error && (
          <ThemedText style={dynamicStyles.errorText}>{error}</ThemedText>
        )}
      </View>
    );
  }
);

ResponsiveTextInput.displayName = 'ResponsiveTextInput';

const createResponsiveStyles = (deviceType: string, spacing: number) => {
  const isMobile = deviceType === 'mobile';
  const isTablet = deviceType === 'tablet';
  const minTouchTarget = DeviceUtils.getMinTouchTargetSize();

  return StyleSheet.create({
    container: {
      marginBottom: spacing,
    },
    label: {
      fontSize: isMobile ? 16 : 14,
      fontWeight: '600',
      marginBottom: spacing * 0.5,
      color: 'white',
    },
    inputContainer: {
      backgroundColor: '#2c2c2e',
      borderRadius: isMobile ? 8 : isTablet ? 10 : 12,
      borderWidth: 2,
      borderColor: 'transparent',
      minHeight: isMobile ? minTouchTarget : isTablet ? 48 : 44,
      justifyContent: 'center',
    },
    input: {
      flex: 1,
      paddingHorizontal: spacing,
      paddingVertical: spacing * 0.75,
      fontSize: isMobile ? 16 : isTablet ? 16 : 14,
      color: 'white',
      textAlignVertical: 'top', // For multiline inputs
    },
    errorContainer: {
      borderColor: '#ff4444',
    },
    disabledContainer: {
      backgroundColor: '#1a1a1c',
      opacity: 0.6,
    },
    errorText: {
      fontSize: isMobile ? 14 : 12,
      color: '#ff4444',
      marginTop: spacing * 0.25,
    },
  });
};

export default ResponsiveTextInput;