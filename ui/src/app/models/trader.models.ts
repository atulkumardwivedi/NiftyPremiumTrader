export interface OptionCandle {
  sessionDate: string;
  candleTime: string;
  stockCode: string;
  exchangeCode: string;
  productType: string;
  expiryDate?: string;
  strike?: number;
  optionType?: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  openInterest: number;
  dataSource: string;
  isComplete: boolean;
  delta?: number;
}

export interface PairSignalDetail {
  pairNumber: number;
  callStrike: number;
  putStrike: number;
  strike: number;
  callOpen: number;
  callClose: number;
  callChangePercent: number;
  putOpen: number;
  putClose: number;
  putChangePercent: number;
  pairSignal: string;
}

export interface PremiumSignal {
  candleTime: string;
  direction: number;
  bullishPairs: number;
  bearishPairs: number;
  selectedAtmStrike: number;
  bullishStrikes: number[];
  bearishStrikes: number[];
  pairDetails: PairSignalDetail[];
}

export interface PendingStrategyAction {
  type: number;
  executeTime: string;
  direction: number;
  optionType: string;
  strike: number;
  reason: string;
}

export interface StrategyTrade {
  hasMarginHedge: boolean;
  marginHedgeStrike?: number;
  marginHedgeEntryPrice?: number;
  marginHedgeEntryTime?: string;
  marginHedgeExitPrice?: number;
  marginHedgeSelectionReason?: string;
  executionStage: string;
  peakRequiredMoney: number;
  entryCapital?: any;
  entryBrokerMargin?: any;
  protectionBrokerMargin?: any;
  appliedSettings?: any;
  riskSetup?: any;
  tradeId: string;
  sessionDate: string;
  direction: number;
  signalTime: string;
  soldOptionType: string;
  soldStrike: number;
  entryTime: string;
  entryPrice: number;
  lots: number;
  lotSize: number;
  quantity: number;
  hedgeDistance: number;
  hedgeStrike?: number;
  hedgeEntryTime?: string;
  hedgeEntryPrice?: number;
  hedgeTriggerPnl?: number;
  exitTime?: string;
  soldExitPrice?: number;
  hedgeExitPrice?: number;
  grossPnl: number;
  costs: number;
  netPnl: number;
  bestMarkPnl: number;
  worstMarkPnl: number;
  exitReason: string;
  soldEntryOrderId: string;
  hedgeEntryOrderId: string;
  soldExitOrderId: string;
  hedgeExitOrderId: string;
  isOpen: boolean;
  isHedged: boolean;
  durationMinutes: number;
  theoreticalMaxLossAfterHedge?: number;
}

export interface StepBacktestResponse {
  dataMessages: string[];
  peakRequiredMoney: number;
  peakRequiredMoneyTime?: string;
  appliedSettings?: any;
  riskSetup?: any;
  sessionId: string;
  date: string;
  expiryDate: string;
  currentTime: string;
  currentIndex: number;
  totalSteps: number;
  signal?: PremiumSignal;
  pendingAction?: PendingStrategyAction;
  activeTrade?: StrategyTrade;
  completedTrades: StrategyTrade[];
  realizedPnl: number;
  unrealizedPnl: number;
}

export interface DayBacktestResult {
  peakRequiredMoney: number;
  peakRequiredMoneyTime?: string;
  appliedSettings?: any;
  riskSetup?: any;
  date: string;
  expiryDate: string;
  trades: StrategyTrade[];
  netPnl: number;
  grossPnl: number;
  costs: number;
  wins: number;
  losses: number;
}

export interface RangeBacktestResult {
  peakRequiredMoney: number;
  stoppedEarly: boolean;
  stopReason: string;
  appliedSettings?: any;
  riskSetup?: any;
  fromDate: string;
  toDate: string;
  days: DayBacktestResult[];
  messages: string[];
  trades: StrategyTrade[];
  netPnl: number;
  grossPnl: number;
  costs: number;
  totalTrades: number;
  winningTrades: number;
  winRate: number;
  averagePnlPerTrade: number;
  hedgedTrades: number;
  hedgeUsageRate: number;
  averageDefinedRiskAfterHedge: number;
  maxDrawdown: number;
  profitableDays: number;
}

export interface HedgePreview {
  optionType: string;
  soldStrike: number;
  hedgeDistance: number;
  hedgeStrike: number;
  premium?: number;
  priceTime?: string;
  priceSource: string;
  isAvailable: boolean;
  message: string;
}

export interface BudgetStatus {
  perMinuteLimit: number;
  date: string;
  used: number;
  limit: number;
  remaining: number;
  reservedForLive: number;
  historicalRemaining: number;
}

export interface LiveRuntimeState {
  accountMargin?: any;
  accountMarginMessage?: string;
  setup?: any;
  margin?: any;
  autoLots?: boolean;
  setupProfile?: string;
  running: boolean;
  executeBrokerOrders: boolean;
  marketDataProvider: string;
  executionBroker: string;
  sessionDate: string;
  expiryDate: string;
  hedgeDistance: number;
  lots: number;
  lotSize: number;
  quantity: number;
  refreshMinutes: number;
  serverIndiaTime: string;
  lastRefreshBoundary?: string;
  expectedCompletedCandle?: string;
  lastProcessedCandle?: string;
  lastSignal?: PremiumSignal;
  activeTrade?: StrategyTrade;
  lastTakenDirection: number;
  sawNeutralSinceLastTrade: boolean;
  cooldownRemaining: number;
  entryStatus: string;
  message: string;
  budgetPlan?: {
    refreshMinutes: number;
    contractsPerRefresh: number;
    estimatedRemainingRefreshes: number;
    estimatedCallsNeeded: number;
    availableCalls: number;
    isDegraded: boolean;
  };
}

export interface LiveStatusResponse {
  state: LiveRuntimeState;
  trades: StrategyTrade[];
}

export interface BrokerPosition {
  tradingSymbol: string;
  instrumentToken: string;
  exchangeSegment: string;
  product: string;
  expiryDate?: string;
  strike?: number;
  optionType: string;
  positionRole: string;
  netQuantity: number;
  buyQuantity: number;
  sellQuantity: number;
  averagePrice: number;
  lastPrice: number;
  pnl: number;
  lotSize: number;
  source: string;
  canExitFromUi: boolean;
  exitRestriction: string;
  isRealtime: boolean;
  lastTickAt?: string;
}

export interface BrokerOrder {
  orderId: string;
  tradingSymbol: string;
  instrumentToken: string;
  exchangeSegment: string;
  side: string;
  orderType: string;
  status: string;
  quantity: number;
  filledQuantity: number;
  price: number;
  averagePrice: number;
  product: string;
  tag: string;
  source: string;
  currentPremium: number;
  isRealtime: boolean;
  lastTickAt?: string;
}

export interface BrokerTrade {
  tradeId: string;
  orderId: string;
  tradingSymbol: string;
  instrumentToken: string;
  exchangeSegment: string;
  side: string;
  quantity: number;
  price: number;
  source: string;
}

export interface BrokerDashboard {
  refreshedAt: string;
  positions: BrokerPosition[];
  orders: BrokerOrder[];
  trades: BrokerTrade[];
  message: string;
  realtimeConnected: boolean;
  realtimeSubscriptions: number;
}

export interface BrokerPositionTick {
  tradingSymbol: string;
  instrumentToken: string;
  exchangeSegment: string;
  lastPrice: number;
  pnl: number;
  averagePrice: number;
  netQuantity: number;
  tickTime: string;
  realtime: boolean;
}

export interface BrokerRealtimeConnection {
  connected: boolean;
  subscribedInstruments: number;
  timestamp: string;
  message: string;
}

export interface BrokerOrderResult {
  orderId: string;
  fillPrice: number;
  tradingSymbol: string;
  instrumentToken: string;
  status: string;
  isFilled: boolean;
}

export interface BacktestProgress {
  runId: string;
  status: string;
  currentDate?: string;
  processedDays: number;
  totalDays: number;
  message: string;
}
