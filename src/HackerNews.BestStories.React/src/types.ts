export type Story = {
  title: string;
  uri: string;
  postedBy: string;
  time: string;
  score: number;
  commentCount: number;
};

export const DEFAULT_PAGE_SIZE = 20;
export const MAX_STORIES = 500;
