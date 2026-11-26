import React from 'react';
import { TouchableOpacity } from 'react-native';
import { useResponsiveLayout } from '@/hooks/useResponsiveLayout';
import { API } from '@/services/api';

// 导入不同平台的VideoCard组件
import VideoCardMobile from './VideoCard.mobile';
import VideoCardTablet from './VideoCard.tablet';
import VideoCardTV from './VideoCard.tv';
import type { PlayabilityStatus } from '@/stores/homeStore';

interface VideoCardProps extends React.ComponentProps<typeof TouchableOpacity> {
  id: string;
  source: string;
  title: string;
  poster: string;
  year?: string;
  rate?: string;
  sourceName?: string;
  progress?: number;
  playTime?: number;
  episodeIndex?: number;
  totalEpisodes?: number;
  playabilityStatus?: PlayabilityStatus;
  playableSourceCount?: number;
  onFocus?: () => void;
  onRecordDeleted?: () => void;
  /** 推荐理由等一行说明,显示在标题下方 */
  reason?: string;
  /** 点击直接起播:有观看记录接着看,没有从第一集开始;不先进详情页。搜索结果不再使用(改回先进详情页),目前没有页面传它 */
  directPlay?: boolean;
  api: API;
}

/**
 * 响应式VideoCard组件
 * 根据设备类型自动选择合适的VideoCard实现
 */
const VideoCard = React.forwardRef<any, VideoCardProps>((props, ref) => {
  const { deviceType } = useResponsiveLayout();

  switch (deviceType) {
    case 'mobile':
      return <VideoCardMobile {...props} ref={ref} />;
    
    case 'tablet':
      return <VideoCardTablet {...props} ref={ref} />;
    
    case 'tv':
    default:
      return <VideoCardTV {...props} ref={ref} />;
  }
});

VideoCard.displayName = 'VideoCard';

export default VideoCard;
