import { useCallback, useEffect, useRef, useState } from 'react';
import AsyncStorage from '@react-native-async-storage/async-storage';

const STORAGE_KEY = 'mytv_nav_collapsed';

/**
 * Pure UI state: whether the navigation chrome (phone tab bar / tablet sidebar) is collapsed.
 * Persisted per surface under one AsyncStorage key; every storage failure is silent and
 * simply leaves the navigation expanded.
 */
export function useNavCollapsed(surface: 'mobile' | 'tablet', initial = false) {
  const [collapsed, setCollapsedState] = useState(initial);
  const touched = useRef(false);

  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const raw = await AsyncStorage.getItem(STORAGE_KEY);
        if (!alive || touched.current || !raw) return;
        const parsed = JSON.parse(raw);
        if (parsed && typeof parsed[surface] === 'boolean') setCollapsedState(parsed[surface]);
      } catch {
        // keep the default
      }
    })();
    return () => {
      alive = false;
    };
  }, [surface]);

  const setCollapsed = useCallback(
    (next: boolean, persist = true) => {
      touched.current = true;
      setCollapsedState(next);
      // 自动收起(例如平板竖屏点导航后)不记住,只有用户按开关才持久化
      if (!persist) return;
      (async () => {
        try {
          const raw = await AsyncStorage.getItem(STORAGE_KEY);
          let parsed: Record<string, boolean> = {};
          try {
            const value = raw ? JSON.parse(raw) : null;
            if (value && typeof value === 'object') parsed = value;
          } catch {
            // overwrite a corrupt value
          }
          parsed[surface] = next;
          await AsyncStorage.setItem(STORAGE_KEY, JSON.stringify(parsed));
        } catch {
          // UI-only preference; ignore
        }
      })();
    },
    [surface]
  );

  return [collapsed, setCollapsed] as const;
}
