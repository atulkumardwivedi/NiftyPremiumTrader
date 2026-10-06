import { CommonModule } from "@angular/common";
import { Component, NgZone, OnDestroy, OnInit } from "@angular/core";
import { FormsModule } from "@angular/forms";
import { Subscription, timer, switchMap } from "rxjs";
import { PremiumChartComponent } from "./components/premium-chart/premium-chart.component";
import {
  BacktestProgress,
  BrokerDashboard,
  BrokerOrder,
  BrokerPosition,
  BrokerPositionTick,
  BrokerRealtimeConnection,
  BudgetStatus,
  DayBacktestResult,
  HedgePreview,
  LiveStatusResponse,
  OptionCandle,
  PairSignalDetail,
  PremiumSignal,
  RangeBacktestResult,
  StepBacktestResponse,
  StrategyTrade,
} from "./models/trader.models";
import { TraderApiService } from "./services/trader-api.service";
import { BrokerRealtimeService } from "./services/broker-realtime.service";

@Component({
  selector: "app-root",
  standalone: true,
  imports: [CommonModule, FormsModule, PremiumChartComponent],
  templateUrl: "./app.component.html",
  styleUrls: ["./app.component.scss"],
})
export class AppComponent implements OnInit, OnDestroy {
  selectedDate = this.toInputDate(new Date());
  expiryDate = "";
  rangeFrom = this.selectedDate;
  rangeTo = this.selectedDate;

  marginHedgeEnabled = false;
  protectionHedgeEnabled = true;
  sellStrikeMode = "Delta50";
  capitalPreview: any;
  capitalLoading = false;
  capitalError = "";
  capitalOptionType = "CE";
  get tradeConfiguration() { return { marginHedgeEnabled: this.marginHedgeEnabled, protectionHedgeEnabled: this.protectionHedgeEnabled, sellStrikeMode: this.sellStrikeMode }; }
  invalidateCapital(): void { this.capitalPreview = undefined; this.sizingPreview = undefined; this.capitalError = ""; }
  refreshCapital(): void {
    this.capitalLoading = true; this.capitalError = ""; this.capitalPreview = undefined;
    this.api.previewBacktestCapital({ date: this.selectedDate, expiryDate: this.expiryDate || null,
      lots: this.lots, lotSize: this.lotSize, hedgeDistance: this.hedgeDistance,
      optionType: this.capitalOptionType, replayTime: this.step?.date === this.selectedDate ? this.step.currentTime : null,
      ...this.tradeConfiguration }).subscribe({
        next: value => { this.capitalPreview = value; this.capitalLoading = false; },
        error: error => { this.capitalLoading = false; this.capitalError = error?.error?.message || "Capital preview unavailable."; }
    });
  }
  hedgeDistance = 300;
  lots = 7;
  lotSize = 65;
  executeBrokerOrders = false;
  autoLots = false;
  setupProfile = "Automatic";
  riskSetup: any;
  sizingPreview: any;
  availableMargin?: number;
  marginLoading = false;
  marginError = "";
  private setupRequest = 0;
  refreshRiskSetup(): void {
    this.invalidateCapital();
    const version = ++this.setupRequest;
    this.api
      .getRiskSetup(this.lots, this.lotSize, this.setupProfile)
      .subscribe({
        next: (value) => {
          if (version === this.setupRequest) {
            this.riskSetup = value;
            this.marginError = "";
          }
        },
        error: (error) => {
          if (version === this.setupRequest) {
            this.riskSetup = undefined;
            this.marginError =
              error?.error?.message || "Unable to calculate settings.";
          }
        },
      });
  }
  refreshMargin(): void {
    this.marginLoading = true;
    this.marginError = "";
    this.sizingPreview = undefined;
    this.api
      .previewSizing(
        this.expiryDate,
        this.lots,
        this.autoLots,
        this.setupProfile,
        this.tradeConfiguration,
        this.hedgeDistance,
      )
      .subscribe({
        next: (value) => {
          this.marginLoading = false;
          this.sizingPreview = value;
          this.availableMargin = value.availableMargin;
          if (value.lots > 0) {
            this.lots = value.lots;
            this.lotSize = value.lotSize;
            this.riskSetup = value.setup;
          }
        },
        error: (error) => {
          this.marginLoading = false;
          this.availableMargin = undefined;
          this.marginError =
            error?.error?.message ||
            "Kotak margin unavailable. Entry will be blocked.";
        },
      });
  }
  refreshFunds(): void {
    this.api.getFunds().subscribe({
      next: (value) => (this.availableMargin = value.availableMargin),
      error: (error) => {
        this.availableMargin = undefined;
        this.marginError =
          error?.error?.message ||
          "Kotak funds unavailable; sign in/configure Kotak.";
      },
    });
  }

  step?: StepBacktestResponse;
  rangeResult?: RangeBacktestResult;
  chartCandles: OptionCandle[] = [];
  chartStrike?: number;
  chartOptionType = "CE";
  hedgePreview?: HedgePreview;
  budget?: BudgetStatus;
  dataAvailability: any;
  dataAvailabilityError = "";
  refreshDataAvailability(): void {
    const date = this.selectedDate, expiry = this.expiryDate;
    this.dataAvailability = undefined;
    this.dataAvailabilityError = "";
    this.api.getDataAvailability(date, expiry).subscribe({
      next: value => { if (date === this.selectedDate && expiry === this.expiryDate) this.dataAvailability = value; },
      error: () => { if (date === this.selectedDate && expiry === this.expiryDate) this.dataAvailabilityError = "Could not read database availability."; }
    });
  }
  dataSelectionChanged(): void { this.invalidateCapital(); this.refreshDataAvailability(); }
  liveStatus?: LiveStatusResponse;
  brokerDashboard?: BrokerDashboard;
  brokerLoading = false;
  brokerError = "";
  brokerMessage = "";
  brokerRealtimeConnected = false;
  brokerRealtimeSubscriptions = 0;
  brokerRealtimeMessage = "Connecting to Kotak realtime feed...";

  manualStrike?: number;
  manualOptionType = "CE";
  manualSide = "BUY";
  manualLots = 7;
  manualLotSize = 65;
  manualOrderType = "MARKET";
  manualLimitPrice?: number;

  exitLimitPrices: Record<string, number | undefined> = {};
  positionsExpanded = true;
  manualOrderExpanded = false;
  workingOrdersExpanded = false;

  backtestStatus = "Idle";
  backtestMessage = "Choose a day replay or a date range to begin.";
  backtestProgress?: BacktestProgress;
  backtestStartedAt?: number;
  backtestFinishedAt?: number;
  private progressSubscription?: Subscription;
  private statusClock?: Subscription;
  statusClockTick = 0;

  get backtestElapsedSeconds(): number {
    if (!this.backtestStartedAt) {
      return 0;
    }
    return Math.floor(
      ((this.backtestFinishedAt ?? Date.now()) - this.backtestStartedAt) / 1000,
    );
  }

  private beginBacktest(message: string): void {
    this.progressSubscription?.unsubscribe();
    this.statusClock?.unsubscribe();
    this.statusClock = timer(0, 1000).subscribe(
      (tick) => (this.statusClockTick = tick),
    );
    this.backtestStatus = "Running";
    this.backtestMessage = message;
    this.backtestProgress = undefined;
    this.backtestStartedAt = Date.now();
    this.backtestFinishedAt = undefined;
  }

  private finishBacktest(status: string, message: string): void {
    this.progressSubscription?.unsubscribe();
    this.statusClock?.unsubscribe();
    this.backtestStatus = status;
    this.backtestMessage = message;
    this.backtestFinishedAt = Date.now();
  }

  private handleBacktestError(error: unknown): void {
    this.handleError(error);
    this.replayBusy = false;
    this.finishBacktest("Error", `Backtest stopped: ${this.error}`);
  }

  loading = false;
  replayBusy = false;
  error = "";
  mode: "backtest" | "live" = "backtest";

  private liveSubscription?: Subscription;
  private lastLiveTradeId = "";
  private lastLiveProcessedCandle = "";
  private lastBrokerRefreshMs = 0;

  hedgePreviewLoading = false;

  constructor(
    private readonly api: TraderApiService,
    private readonly brokerRealtime: BrokerRealtimeService,
    private readonly zone: NgZone,
  ) {}

  ngOnInit(): void {
    this.refreshRiskSetup();
    this.refreshBudget();
    this.refreshBrokerDashboard(false);
    this.startBrokerRealtime();
  }

  ngOnDestroy(): void {
    this.liveSubscription?.unsubscribe();
    this.progressSubscription?.unsubscribe();
    this.statusClock?.unsubscribe();
    void this.brokerRealtime.stop();
  }

  startBacktest(): void {
    if (this.loading || this.replayBusy) {
      return;
    }
    this.beginBacktest("Loading the selected day from the database/API.");
    this.loading = true;
    this.error = "";
    this.mode = "backtest";
    this.liveSubscription?.unsubscribe();

    this.api
      .startBacktest(
        this.selectedDate,
        this.expiryDate,
        this.hedgeDistance,
        this.lots,
        this.lotSize,
        this.setupProfile,
        this.tradeConfiguration,
      )
      .subscribe({
        next: (response) => {
          this.step = response;
          this.finishBacktest(
            "Replay ready",
            "Day loaded. Use Next candle to replay; no signal is a valid candle outcome.",
          );
          this.expiryDate = response.expiryDate;
          this.rangeResult = undefined;
          this.loading = false;
          this.selectChartFromStep();
          this.refreshBudget();
        },
        error: (error) => this.handleBacktestError(error),
      });
  }

  move(direction: number): void {
    if (!this.step || this.replayBusy) {
      return;
    }

    this.beginBacktest("Replaying the selected candle.");
    this.replayBusy = true;
    this.error = "";

    this.api.stepBacktest(this.step.sessionId, direction).subscribe({
      next: (response) => {
        this.step = response;
        this.replayBusy = false;
        const finished = response.currentIndex >= response.totalSteps - 1;
        this.finishBacktest(
          finished ? "Completed" : "Replay paused",
          finished
            ? "Day replay completed."
            : `Candle ${response.currentIndex + 1} of ${response.totalSteps}. Ready for the next step.`,
        );
        this.selectChartFromStep();
      },
      error: (error) => {
        this.replayBusy = false;
        this.handleBacktestError(error);
      },
    });
  }

  runRange(): void {
    if (this.loading || this.replayBusy) {
      return;
    }
    this.beginBacktest("Starting the requested date range.");
    const runId = crypto.randomUUID();
    this.progressSubscription = timer(0, 2000)
      .pipe(switchMap(() => this.api.getBacktestProgress(runId)))
      .subscribe({
        next: (progress) => {
          this.backtestProgress = progress;
          this.backtestMessage = progress.message;
        },
        error: () => {
          this.backtestMessage =
            "Progress connection unavailable. The backtest request is still pending.";
        },
      });
    this.loading = true;
    this.error = "";

    this.api
      .runRange(
        this.rangeFrom,
        this.rangeTo,
        this.hedgeDistance,
        this.lots,
        this.lotSize,
        this.setupProfile,
        runId,
        this.tradeConfiguration,
      )
      .subscribe({
        next: (result) => {
          this.rangeResult = result;
          const status = result.stoppedEarly
            ? "Stopped early"
            : result.messages.length
              ? "Completed with warnings"
              : "Completed";
          const message = result.stoppedEarly
            ? `Partial results: ${result.stopReason}`
            : `${result.days.length} sessions and ${result.totalTrades} trades. ${result.messages.length ? "Some dates were skipped; review the warnings." : "All requested sessions processed."}`;
          this.finishBacktest(status, message);
          this.loading = false;
          this.refreshBudget();
        },
        error: (error) => this.handleBacktestError(error),
      });
  }

  loadChart(): void {
    if (!this.expiryDate || !this.chartStrike) {
      return;
    }

    this.api
      .getCandles(
        this.selectedDate,
        this.expiryDate,
        this.chartStrike,
        this.chartOptionType,
        this.mode === "live",
      )
      .subscribe({
        next: (candles) => {
          this.chartCandles = candles;
        },
        error: (error) => this.handleError(error),
      });
  }

  changeHedgeDistance(): void {
    this.error = "";
    this.hedgePreview = undefined;

    const reference = this.getHedgeReference();

    if (!reference || !this.expiryDate) {
      return;
    }

    this.hedgePreviewLoading = true;

    this.api
      .getHedgePreview(
        this.selectedDate,
        this.expiryDate,
        reference.strike,
        reference.optionType,
        this.hedgeDistance,
        this.mode === "backtest" ? this.step?.currentTime : undefined,
        this.mode === "live",
      )
      .subscribe({
        next: (preview) => {
          this.hedgePreview = preview;
          this.hedgePreviewLoading = false;
          this.refreshBudget();
        },
        error: (error) => {
          this.hedgePreviewLoading = false;
          this.handleError(error);
        },
      });
  }

  startLive(): void {
    this.error = "";
    this.mode = "live";
    this.step = undefined;

    this.api
      .startLive(
        this.expiryDate,
        this.hedgeDistance,
        this.lots,
        this.lotSize,
        this.executeBrokerOrders,
        false,
        this.setupProfile,
        this.tradeConfiguration,
      )
      .subscribe({
        next: (state) => {
          this.lots = state.lots;
          this.lotSize = state.lotSize;
          this.riskSetup = state.setup;
          this.sizingPreview = state.margin;
          this.selectedDate = state.sessionDate;
          this.expiryDate = state.expiryDate;
          this.chartCandles = [];
          this.chartStrike = undefined;
          this.hedgePreview = undefined;
          this.lastLiveTradeId = "";
          this.lastLiveProcessedCandle = "";

          this.liveStatus = {
            state,
            trades: [],
          };

          if (!this.manualStrike && state?.lastSignal?.selectedAtmStrike) {
            this.manualStrike = state.lastSignal.selectedAtmStrike;
          }

          this.manualLotSize = this.lotSize;
          this.beginLivePolling();
          this.refreshBrokerDashboard(false);
          this.refreshBudget();
        },
        error: (error) => this.handleError(error),
      });
  }

  stopLive(): void {
    this.api.stopLive().subscribe({
      next: (state) => {
        if (this.liveStatus) {
          this.liveStatus.state = state;
        }

        this.liveSubscription?.unsubscribe();
      },
      error: (error) => this.handleError(error),
    });
  }

  refreshBrokerDashboard(showError = true): void {
    if (this.brokerLoading) {
      return;
    }

    this.brokerLoading = true;

    this.api.getBrokerDashboard().subscribe({
      next: (dashboard) => {
        this.applyBrokerDashboard(dashboard);
        this.brokerLoading = false;
        this.brokerError = "";
      },
      error: (error) => {
        this.brokerLoading = false;

        const value = error?.error?.message ?? error?.error ?? error?.message;

        this.brokerError =
          typeof value === "string"
            ? value
            : "Unable to refresh Kotak broker activity.";

        if (showError) {
          this.error = this.brokerError;
        }
      },
    });
  }

  placeManualTrade(): void {
    this.brokerError = "";
    this.brokerMessage = "";

    if (!this.executeBrokerOrders) {
      this.brokerError =
        "Enable Send real Kotak Neo orders before placing a manual UI order.";
      return;
    }

    if (!this.expiryDate || !this.manualStrike || this.manualStrike <= 0) {
      this.brokerError = "Expiry date and strike are required.";
      return;
    }

    if (this.manualStrike % 50 !== 0) {
      this.brokerError = "NIFTY option strike must be a multiple of 50.";
      return;
    }

    if (this.manualStrike < 10000) {
      const suggestedStrike = this.manualStrike * 10;

      this.brokerError =
        `Strike ${this.manualStrike} does not look like a current NIFTY strike. ` +
        `Did you mean ${suggestedStrike}?`;
      return;
    }

    if (
      this.manualOrderType === "LIMIT" &&
      (!this.manualLimitPrice || this.manualLimitPrice <= 0)
    ) {
      this.brokerError = "Enter a positive limit price.";
      return;
    }

    this.brokerLoading = true;

    this.api
      .placeManualBrokerOrder({
        expiryDate: this.expiryDate,
        strike: this.manualStrike,
        optionType: this.manualOptionType,
        side: this.manualSide,
        lots: this.manualLots,
        lotSize: this.manualLotSize,
        orderType: this.manualOrderType,
        limitPrice:
          this.manualOrderType === "LIMIT" ? this.manualLimitPrice : undefined,
      })
      .subscribe({
        next: (result) => {
          this.brokerLoading = false;
          this.brokerMessage = result.isFilled
            ? `Kotak order ${result.orderId} filled at ${result.fillPrice}.`
            : `Kotak order ${result.orderId} placed with status ${result.status}.`;
          this.refreshBrokerDashboard(false);
        },
        error: (error) => {
          this.brokerLoading = false;
          this.handleBrokerError(error);
        },
      });
  }

  exitBrokerPosition(
    position: BrokerPosition,
    orderType: "MARKET" | "LIMIT",
  ): void {
    this.brokerError = "";
    this.brokerMessage = "";

    if (!this.executeBrokerOrders) {
      this.brokerError =
        "Enable Send real Kotak Neo orders before sending an exit order.";
      return;
    }

    const limitPrice = this.exitLimitPrices[position.tradingSymbol];

    if (orderType === "LIMIT" && (!limitPrice || limitPrice <= 0)) {
      this.brokerError = "Enter a positive exit limit price.";
      return;
    }

    if (
      (position.source === "SYSTEM" || position.source === "MIXED") &&
      !window.confirm(
        "This row contains a strategy position. The exit order will be sent to Kotak and automatic strategy trading will be stopped immediately to prevent a duplicate exit. Continue?",
      )
    ) {
      return;
    }

    this.brokerLoading = true;

    this.api
      .exitBrokerPosition({
        tradingSymbol: position.tradingSymbol,
        orderType,
        limitPrice: orderType === "LIMIT" ? limitPrice : undefined,
      })
      .subscribe({
        next: (result) => {
          this.brokerLoading = false;
          this.brokerMessage = result.isFilled
            ? `Exit order ${result.orderId} filled at ${result.fillPrice}.`
            : `Exit order ${result.orderId} placed with status ${result.status}.`;
          this.refreshBrokerDashboard(false);
        },
        error: (error) => {
          this.brokerLoading = false;
          this.handleBrokerError(error);
        },
      });
  }

  viewPositionChart(position: BrokerPosition): void {
    this.brokerError = "";

    if (!position.strike || !position.optionType || !position.expiryDate) {
      this.brokerError =
        "Kotak instrument metadata is not available for this position yet. Refresh Kotak and try again.";
      return;
    }

    this.selectedDate = this.liveStatus?.state.sessionDate ?? this.selectedDate;
    this.expiryDate = position.expiryDate;
    this.chartStrike = position.strike;
    this.chartOptionType = position.optionType;
    this.chartCandles = [];

    this.api
      .ensureContract(
        this.selectedDate,
        this.expiryDate,
        position.strike,
        position.optionType,
        true,
      )
      .subscribe({
        next: () => {
          this.loadChart();
          this.refreshBudget();
        },
        error: (error) => this.handleBrokerError(error),
      });
  }

  positionSide(position: BrokerPosition): string {
    return position.netQuantity < 0 ? "SHORT" : "LONG";
  }

  positionLots(position: BrokerPosition): number {
    const lotSize = Math.max(1, position.lotSize || 1);
    return Math.abs(position.netQuantity) / lotSize;
  }

  sourceLabel(source: string): string {
    if (source === "SYSTEM") {
      return "SYSTEM";
    }

    if (source === "MANUAL_UI") {
      return "MANUAL UI";
    }

    if (source === "MANUAL_APP") {
      return "MANUAL APP";
    }

    return source || "-";
  }

  refreshBudget(): void {
    this.refreshDataAvailability();
    this.api.getBudget().subscribe({
      next: (status) => {
        this.budget = status;
      },
    });
  }

  get stepSignalPairDetails(): PairSignalDetail[] {
    return this.step?.signal?.pairDetails ?? [];
  }

  get liveSignalPairDetails(): PairSignalDetail[] {
    return this.liveStatus?.state.lastSignal?.pairDetails ?? [];
  }

  get brokerPositions(): BrokerPosition[] {
    return this.brokerDashboard?.positions ?? [];
  }

  get totalBrokerPnl(): number {
    return this.brokerPositions.reduce(
      (total, position) => total + position.pnl,
      0,
    );
  }

  get workingOrders(): BrokerOrder[] {
    return this.brokerDashboard?.orders ?? [];
  }

  get strategyShortPosition(): BrokerPosition | undefined {
    return this.brokerPositions.find(
      (position) => position.positionRole === "STRATEGY SHORT",
    );
  }

  get strategyHedgePosition(): BrokerPosition | undefined {
    return this.brokerPositions.find(
      (position) => position.positionRole === "STRATEGY HEDGE" && position.strike === this.activeLiveTrade?.hedgeStrike && position.optionType === this.activeLiveTrade?.soldOptionType,
    );
  }

  get strategyMarginHedgePosition(): BrokerPosition | undefined {
    return this.brokerPositions.find(position => position.positionRole === "STRATEGY HEDGE" && position.strike === this.activeLiveTrade?.marginHedgeStrike && position.optionType === this.activeLiveTrade?.soldOptionType);
  }

  get plannedSystemHedgeStrike(): number | undefined {
    const trade = this.activeLiveTrade;

    if (!trade) {
      return undefined;
    }

    if (trade.hedgeStrike && trade.hedgeStrike > 0) {
      return trade.hedgeStrike;
    }

    if (trade.soldOptionType === "CE") {
      return trade.soldStrike + this.hedgeDistance;
    }

    if (trade.soldOptionType === "PE") {
      return trade.soldStrike - this.hedgeDistance;
    }

    return undefined;
  }

  get completedDayTrades(): StrategyTrade[] {
    return this.step?.completedTrades ?? [];
  }

  get rangeMessages(): string[] {
    return this.rangeResult?.messages ?? [];
  }

  get rangeDays(): DayBacktestResult[] {
    return this.rangeResult?.days ?? [];
  }

  get rangeTrades(): StrategyTrade[] {
    return this.rangeResult?.trades ?? [];
  }

  get chartSignal(): PremiumSignal | undefined {
    if (this.mode === "backtest") {
      return this.step?.signal;
    }

    return this.liveStatus?.state.lastSignal;
  }

  get hasHedgeReference(): boolean {
    return this.getHedgeReference() !== undefined;
  }

  get configuredQuantity(): number {
    return Math.max(0, this.lots) * Math.max(0, this.lotSize);
  }

  get activeLiveTrade(): StrategyTrade | undefined {
    return this.liveStatus?.state?.activeTrade;
  }

  get computedHedgeStrike(): number | undefined {
    const reference = this.getHedgeReference();

    if (!reference) {
      return undefined;
    }

    if (reference.optionType === "CE") {
      return reference.strike + this.hedgeDistance;
    }

    return reference.strike - this.hedgeDistance;
  }

  get computedHedgeOptionType(): string {
    return this.getHedgeReference()?.optionType ?? "";
  }

  get currentTrade(): StrategyTrade | undefined {
    if (this.mode === "live") {
      const active = this.liveStatus?.state?.activeTrade as
        StrategyTrade | undefined;

      if (active) {
        return active;
      }

      return this.liveStatus?.trades?.at(-1);
    }

    if (this.step?.activeTrade) {
      return this.step.activeTrade;
    }

    return this.step?.completedTrades?.at(-1);
  }

  get backtestCurrentTrade(): StrategyTrade | undefined {
    if (this.mode !== "backtest") {
      return undefined;
    }

    if (this.step?.activeTrade) {
      return this.step.activeTrade;
    }

    return this.step?.completedTrades?.at(-1);
  }

  get chartTrades(): StrategyTrade[] {
    if (this.mode === "live") {
      const trades = this.liveStatus?.trades ?? [];
      const active = this.liveStatus?.state?.activeTrade as
        StrategyTrade | undefined;

      if (active && !trades.some((x) => x.tradeId === active.tradeId)) {
        return [...trades, active];
      }

      return trades;
    }

    const completed = this.step?.completedTrades ?? [];
    const active = this.step?.activeTrade ? [this.step.activeTrade] : [];
    return [...completed, ...active];
  }

  signalText(): string {
    const direction =
      this.step?.signal?.direction ??
      this.liveStatus?.state?.lastSignal?.direction;

    if (direction === 1) {
      return "BULLISH → SELL PE";
    }

    if (direction === 2) {
      return "BEARISH → SELL CE";
    }

    return "NO ENTRY";
  }

  pendingActionText(): string {
    const action = this.step?.pendingAction;

    if (!action) {
      return "-";
    }

    if (action.type === 1) {
      return `ENTER SELL ${action.strike} ${action.optionType}`;
    }

    if (action.type === 2) {
      return `BUY HEDGE ${action.strike} ${action.optionType}`;
    }

    if (action.type === 3) {
      return `EXIT ${action.reason}`;
    }

    return "-";
  }

  private selectChartFromStep(): void {
    const trade = this.currentTrade;

    if (trade) {
      this.selectChartContract(trade.soldStrike, trade.soldOptionType);

      this.refreshHedgePreviewIfReferenceChanged(
        trade.soldStrike,
        trade.soldOptionType,
      );
      return;
    }

    const pending = this.step?.pendingAction;

    if (pending && pending.type === 1) {
      this.selectChartContract(pending.strike, pending.optionType);

      this.refreshHedgePreviewIfReferenceChanged(
        pending.strike,
        pending.optionType,
      );
      return;
    }

    const signal = this.step?.signal;

    if (signal && signal.selectedAtmStrike > 0) {
      const optionType = signal.direction === 1 ? "PE" : "CE";

      this.selectChartContract(signal.selectedAtmStrike, optionType);
    }
  }

  private refreshHedgePreviewIfReferenceChanged(
    soldStrike: number,
    optionType: string,
  ): void {
    const samePreview =
      !!this.hedgePreview &&
      this.hedgePreview.soldStrike === soldStrike &&
      this.hedgePreview.optionType === optionType &&
      this.hedgePreview.hedgeDistance === this.hedgeDistance;

    if (!samePreview && !this.hedgePreviewLoading) {
      this.changeHedgeDistance();
    }
  }

  private selectChartContract(strike: number, optionType: string): void {
    const changed =
      this.chartStrike !== strike || this.chartOptionType !== optionType;

    this.chartStrike = strike;
    this.chartOptionType = optionType;

    if (changed || this.chartCandles.length === 0) {
      this.loadChart();
    }
  }

  private getHedgeReference():
    { strike: number; optionType: string } | undefined {
    if (this.mode === "live") {
      const active = this.liveStatus?.state?.activeTrade;

      if (active) {
        return {
          strike: active.soldStrike,
          optionType: active.soldOptionType,
        };
      }

      const signal = this.liveStatus?.state?.lastSignal;

      if (signal && signal.direction > 0 && signal.selectedAtmStrike > 0) {
        return {
          strike: signal.selectedAtmStrike,
          optionType: signal.direction === 1 ? "PE" : "CE",
        };
      }

      if (this.chartStrike) {
        return {
          strike: this.chartStrike,
          optionType: this.chartOptionType,
        };
      }

      return undefined;
    }

    const trade = this.currentTrade;

    if (trade) {
      return {
        strike: trade.soldStrike,
        optionType: trade.soldOptionType,
      };
    }

    const pending = this.step?.pendingAction;

    if (pending && pending.type === 1) {
      return {
        strike: pending.strike,
        optionType: pending.optionType,
      };
    }

    if (this.chartStrike) {
      return {
        strike: this.chartStrike,
        optionType: this.chartOptionType,
      };
    }

    return undefined;
  }

  private beginLivePolling(): void {
    this.liveSubscription?.unsubscribe();

    this.liveSubscription = timer(0, 5000).subscribe(() => {
      this.api.getLiveStatus().subscribe({
        next: (status) => {
          const previousBoundary = this.lastLiveProcessedCandle;
          const currentBoundary =
            status.state?.expectedCompletedCandle ??
            status.state?.lastProcessedCandle ??
            "";
          const candleChanged =
            !!currentBoundary && currentBoundary !== previousBoundary;

          this.liveStatus = status;
          if (status.state?.setup) this.riskSetup = status.state.setup;
          if (status.state?.margin) {
            this.sizingPreview = status.state.margin;
            this.availableMargin = status.state.margin.availableMargin;
          }
          if (status.state?.running) {
            this.lots = status.state.lots;
            this.lotSize = status.state.lotSize;
          }
          this.lastLiveProcessedCandle = currentBoundary;
          this.selectedDate = status.state?.sessionDate ?? this.selectedDate;
          this.expiryDate = status.state?.expiryDate ?? this.expiryDate;
          this.refreshBudget();

          const activeTrade = status.state?.activeTrade;
          const latestTrade = activeTrade ?? status.trades.at(-1);

          if (activeTrade) {
            const sameChart =
              this.chartStrike === activeTrade.soldStrike &&
              this.chartOptionType === activeTrade.soldOptionType;

            this.selectChartContract(
              activeTrade.soldStrike,
              activeTrade.soldOptionType,
            );

            if (candleChanged && sameChart) {
              this.loadChart();
            }

            if (activeTrade.tradeId !== this.lastLiveTradeId) {
              this.lastLiveTradeId = activeTrade.tradeId;
              this.changeHedgeDistance();
            }

            return;
          }

          const signal = status.state?.lastSignal;

          if (signal && signal.selectedAtmStrike > 0) {
            const optionType =
              signal.direction === 1
                ? "PE"
                : signal.direction === 2
                  ? "CE"
                  : this.chartOptionType || "CE";

            const sameChart =
              this.chartStrike === signal.selectedAtmStrike &&
              this.chartOptionType === optionType;

            this.selectChartContract(signal.selectedAtmStrike, optionType);

            if (candleChanged && sameChart) {
              this.loadChart();
            }
          } else if (candleChanged && this.chartStrike) {
            this.loadChart();
          }

          if (latestTrade) {
            this.lastLiveTradeId = latestTrade.tradeId;
          }
        },
        error: (error) => this.handleError(error),
      });
    });
  }

  private startBrokerRealtime(): void {
    void this.brokerRealtime
      .start(
        (dashboard) =>
          this.zone.run(() => this.applyBrokerDashboard(dashboard)),
        (tick) => this.zone.run(() => this.applyBrokerPositionTick(tick)),
        (status) => this.zone.run(() => this.applyBrokerRealtimeStatus(status)),
      )
      .catch((error) => {
        const message =
          error?.message ?? "Unable to connect to the broker realtime hub.";

        this.brokerRealtimeConnected = false;
        this.brokerRealtimeMessage = message;
      });
  }

  private applyBrokerDashboard(dashboard: BrokerDashboard): void {
    this.brokerDashboard = dashboard;
    this.brokerRealtimeConnected = dashboard.realtimeConnected;
    this.brokerRealtimeSubscriptions = dashboard.realtimeSubscriptions;
    this.lastBrokerRefreshMs = Date.now();

    const detectedLotSize = dashboard.positions
      .map((position) => position.lotSize)
      .find((value) => value > 0);

    if (detectedLotSize) {
      this.manualLotSize = detectedLotSize;
    }

    for (const position of dashboard.positions) {
      if (
        this.exitLimitPrices[position.tradingSymbol] == null &&
        position.lastPrice > 0
      ) {
        this.exitLimitPrices[position.tradingSymbol] = position.lastPrice;
      }
    }
  }

  private applyBrokerPositionTick(tick: BrokerPositionTick): void {
    const position = this.brokerDashboard?.positions.find(
      (x) =>
        x.instrumentToken === tick.instrumentToken &&
        this.normalizeExchange(x.exchangeSegment) ===
          this.normalizeExchange(tick.exchangeSegment),
    );

    if (position) {
      position.lastPrice = tick.lastPrice;
      position.pnl = tick.pnl;
      position.isRealtime = tick.realtime;
      position.lastTickAt = tick.tickTime;
    }

    const matchingOrders =
      this.brokerDashboard?.orders.filter(
        (x) =>
          x.instrumentToken === tick.instrumentToken &&
          this.normalizeExchange(x.exchangeSegment) ===
            this.normalizeExchange(tick.exchangeSegment),
      ) ?? [];

    for (const order of matchingOrders) {
      order.currentPremium = tick.lastPrice;
      order.isRealtime = tick.realtime;
      order.lastTickAt = tick.tickTime;
    }
  }

  private applyBrokerRealtimeStatus(status: BrokerRealtimeConnection): void {
    this.brokerRealtimeConnected = status.connected;
    this.brokerRealtimeSubscriptions = status.subscribedInstruments;
    this.brokerRealtimeMessage = status.message;
  }

  private normalizeExchange(value: string): string {
    const normalized = (value || "").toLowerCase();

    if (
      normalized === "nfo" ||
      normalized === "nsefo" ||
      normalized === "nse_fo"
    ) {
      return "nse_fo";
    }

    return normalized;
  }

  private handleBrokerError(error: any): void {
    const value = error?.error?.message ?? error?.error ?? error?.message;

    this.brokerError =
      typeof value === "string" ? value : "Kotak broker request failed.";
  }

  replayTimeIst(value: string): string {
    return new Date(value).toLocaleTimeString("en-IN", {
      hour: "2-digit",
      minute: "2-digit",
      hour12: false,
      timeZone: "Asia/Kolkata",
    });
  }

  private handleError(error: any): void {
    this.loading = false;

    const value = error?.error?.message ?? error?.error ?? error?.message;

    this.error = typeof value === "string" ? value : "Request failed.";
  }

  private toInputDate(date: Date): string {
    const year = date.getFullYear();
    const month = `${date.getMonth() + 1}`.padStart(2, "0");
    const day = `${date.getDate()}`.padStart(2, "0");

    return `${year}-${month}-${day}`;
  }
}
