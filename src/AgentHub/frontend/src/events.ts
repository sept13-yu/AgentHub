/** 订阅 Runtime SSE，把 dashboard-refresh 转成既有的 window 事件 agenthub-refresh。EventSource 自带重连。 */
export function startEventStream(): void {
  if (typeof EventSource === 'undefined') return
  const es = new EventSource('/api/events')
  es.addEventListener('dashboard-refresh', () =>
    window.dispatchEvent(new CustomEvent('agenthub-refresh')),
  )
  // settings-saved 本步不消费；预留给主题跨窗同步
}
