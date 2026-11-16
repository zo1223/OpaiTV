import { useEffect, useRef, useCallback } from "react";
import type { HWEvent } from "react-native";
import usePlayerStore from "@/stores/playerStore";
import { useSafeTVEventHandler } from "@/hooks/useSafeTVEventHandler";

const SEEK_STEP = 20 * 1000; // 快进/快退的时间步长（毫秒）

// 定时器延迟时间（毫秒）
const CONTROLS_TIMEOUT = 5000;

/**
 * 管理播放器控件的显示/隐藏、遥控器事件和自动隐藏定时器。
 * @returns onScreenPress - 一个函数，用于处理屏幕点击事件，以显示控件并重置定时器。
 */
export const useTVRemoteHandler = () => {
  const { showControls, setShowControls, showEpisodeModal, showSourceModal, showSpeedModal, togglePlayPause, seek } =
    usePlayerStore();

  const controlsTimer = useRef<NodeJS.Timeout | null>(null);
  const fastForwardIntervalRef = useRef<NodeJS.Timeout | null>(null);

  // 重置或启动隐藏控件的定时器
  const resetTimer = useCallback(() => {
    // 清除之前的定时器
    if (controlsTimer.current) {
      clearTimeout(controlsTimer.current);
    }
    // 设置新的定时器
    controlsTimer.current = setTimeout(() => {
      setShowControls(false);
    }, CONTROLS_TIMEOUT);
  }, [setShowControls]);

  // 当控件显示时，启动定时器
  useEffect(() => {
    if (showControls) {
      resetTimer();
    } else {
      // 如果控件被隐藏，清除定时器
      if (controlsTimer.current) {
        clearTimeout(controlsTimer.current);
      }
    }

    // 组件卸载时清除定时器
    return () => {
      if (controlsTimer.current) {
        clearTimeout(controlsTimer.current);
      }
    };
  }, [showControls, resetTimer]);

  // 组件卸载时清除快进定时器
  useEffect(() => {
    return () => {
      if (fastForwardIntervalRef.current) {
        clearInterval(fastForwardIntervalRef.current);
      }
    };
  }, []);

  // 处理遥控器事件
  const handleTVEvent = useCallback(
    (event: HWEvent) => {
      if (showEpisodeModal) {
        return;
      }

      if (event.eventType === "longRight" || event.eventType === "longLeft") {
        if (event.eventKeyAction === 1) {
          if (fastForwardIntervalRef.current) {
            clearInterval(fastForwardIntervalRef.current);
            fastForwardIntervalRef.current = null;
          }
        }
      }

      resetTimer();

      if (showControls || showSourceModal || showSpeedModal) {
        // 如果控制条已显示，则不处理后台的快进/快退等操作
        // 避免与控制条上的按钮焦点冲突。
        // 换源/倍速弹窗同理:Modal 打开时遥控器按键仍会到达这里,而控制条 5 秒后会自动隐藏,
        // 此后弹窗内的方向键/确认键不能被当作快进快退或暂停
        return;
      }

      switch (event.eventType) {
        case "select":
          togglePlayPause();
          setShowControls(true);
          break;
        case "left":
          seek(-SEEK_STEP); // 快退15秒
          break;
        case "longLeft":
          if (!fastForwardIntervalRef.current && event.eventKeyAction === 0) {
            fastForwardIntervalRef.current = setInterval(() => {
              seek(-SEEK_STEP); 
            }, 200);
          }
          break;
        case "right":
          seek(SEEK_STEP);
          break;
        case "longRight":
          // 长按开始: 启动连续快进
          if (!fastForwardIntervalRef.current && event.eventKeyAction === 0) {
            fastForwardIntervalRef.current = setInterval(() => {
              seek(SEEK_STEP); 
            }, 200);
          }
          break;
        case "down":
          setShowControls(true);
          break;
      }
    },
    [
      showControls,
      showEpisodeModal,
      showSourceModal,
      showSpeedModal,
      setShowControls,
      resetTimer,
      togglePlayPause,
      seek,
    ]
  );

  useSafeTVEventHandler(handleTVEvent);

  // 处理屏幕点击事件
  const onScreenPress = () => {
    // 切换控件的显示状态
    const newShowControls = !showControls;
    setShowControls(newShowControls);

    // 如果控件变为显示状态，则重置定时器
    if (newShowControls) {
      resetTimer();
    }
  };

  return { onScreenPress };
};
