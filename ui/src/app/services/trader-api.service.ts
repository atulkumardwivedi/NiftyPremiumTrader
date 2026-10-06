import { HttpClient, HttpParams } from "@angular/common/http";
import { Injectable } from "@angular/core";
import { Observable } from "rxjs";
import {
  BacktestProgress,
  BrokerDashboard,
  BrokerOrderResult,
  BudgetStatus,
  HedgePreview,
  LiveStatusResponse,
  OptionCandle,
  RangeBacktestResult,
  StepBacktestResponse,
} from "../models/trader.models";

@Injectable({ providedIn: "root" })
export class TraderApiService {
  private readonly baseUrl = "http://localhost:5080/api";

  constructor(private readonly http: HttpClient) {}

  startBacktest(
    date: string,
    expiryDate: string,
    hedgeDistance: number,
    lots: number,
    lotSize: number,
    setupProfile = "Automatic",
    configuration = { marginHedgeEnabled: false, protectionHedgeEnabled: true, sellStrikeMode: "Delta50" },
  ): Observable<StepBacktestResponse> {
    return this.http.post<StepBacktestResponse>(
      `${this.baseUrl}/backtest/start`,
      {
        date,
        expiryDate: expiryDate || null,
        hedgeDistance,
        lots,
        lotSize,
        setupProfile,
        ...configuration,
      },
    );
  }

  stepBacktest(
    sessionId: string,
    direction: number,
  ): Observable<StepBacktestResponse> {
    const params = new HttpParams().set("direction", direction);

    return this.http.post<StepBacktestResponse>(
      `${this.baseUrl}/backtest/step/${sessionId}`,
      null,
      { params },
    );
  }

  runRange(
    fromDate: string,
    toDate: string,
    hedgeDistance: number,
    lots: number,
    lotSize: number,
    setupProfile = "Automatic",
    runId = "",
    configuration = { marginHedgeEnabled: false, protectionHedgeEnabled: true, sellStrikeMode: "Delta50" },
  ): Observable<RangeBacktestResult> {
    return this.http.post<RangeBacktestResult>(
      `${this.baseUrl}/backtest/range?runId=${runId}`,
      {
        fromDate,
        toDate,
        hedgeDistance,
        lots,
        lotSize,
        setupProfile,
        ...configuration,
      },
    );
  }

  getBacktestProgress(runId: string): Observable<BacktestProgress> {
    return this.http.get<BacktestProgress>(
      `${this.baseUrl}/backtest/progress/${runId}`,
    );
  }

  ensureContract(
    date: string,
    expiryDate: string,
    strike: number,
    optionType: string,
    liveMode: boolean,
  ): Observable<{ candleCount: number }> {
    const params = new HttpParams()
      .set("date", date)
      .set("expiryDate", expiryDate)
      .set("strike", strike)
      .set("optionType", optionType)
      .set("liveMode", liveMode);

    return this.http.post<{ candleCount: number }>(
      `${this.baseUrl}/data/ensure-contract`,
      null,
      { params },
    );
  }

  getCandles(
    date: string,
    expiryDate: string,
    strike: number,
    optionType: string,
    liveMode: boolean,
  ): Observable<OptionCandle[]> {
    const params = new HttpParams()
      .set("date", date)
      .set("expiryDate", expiryDate)
      .set("strike", strike)
      .set("optionType", optionType)
      .set("liveMode", liveMode);

    return this.http.get<OptionCandle[]>(`${this.baseUrl}/data/candles`, {
      params,
    });
  }

  getHedgePreview(
    date: string,
    expiryDate: string,
    soldStrike: number,
    optionType: string,
    hedgeDistance: number,
    replayTime?: string,
    liveMode = false,
  ): Observable<HedgePreview> {
    let params = new HttpParams()
      .set("date", date)
      .set("expiryDate", expiryDate)
      .set("soldStrike", soldStrike)
      .set("optionType", optionType)
      .set("hedgeDistance", hedgeDistance)
      .set("liveMode", liveMode);

    if (replayTime) {
      params = params.set("replayTime", replayTime);
    }

    return this.http.get<HedgePreview>(`${this.baseUrl}/data/hedge-preview`, {
      params,
    });
  }

  getDataAvailability(date: string, expiryDate: string): Observable<any> {
    let params = new HttpParams().set("date", date);
    if (expiryDate) params = params.set("expiryDate", expiryDate);
    return this.http.get(`${this.baseUrl}/data/availability`, { params });
  }

  getBudget(): Observable<BudgetStatus> {
    return this.http.get<BudgetStatus>(`${this.baseUrl}/data/budget`);
  }

  getRiskSetup(
    lots: number,
    lotSize: number,
    profile: string,
  ): Observable<any> {
    return this.http.get(`${this.baseUrl}/sizing/setup`, {
      params: new HttpParams()
        .set("lots", lots)
        .set("lotSize", lotSize)
        .set("profile", profile),
    });
  }
  getFunds(): Observable<any> {
    return this.http.get(`${this.baseUrl}/sizing/funds`);
  }
  previewSizing(
    expiryDate: string,
    lots: number,
    automatic: boolean,
    profile: string,
    configuration = { marginHedgeEnabled: false, protectionHedgeEnabled: true, sellStrikeMode: "Delta50" },
    hedgeDistance = 300,
  ): Observable<any> {
    let params = new HttpParams()
      .set("lots", lots)
      .set("automatic", automatic)
      .set("profile", profile)
      .set("hedgeDistance", hedgeDistance)
      .set("marginHedgeEnabled", configuration.marginHedgeEnabled)
      .set("protectionHedgeEnabled", configuration.protectionHedgeEnabled)
      .set("sellStrikeMode", configuration.sellStrikeMode);
    if (expiryDate) params = params.set("expiryDate", expiryDate);
    return this.http.post(`${this.baseUrl}/sizing/preview`, null, { params });
  }

  previewBacktestCapital(request: any): Observable<any> {
    return this.http.post(`${this.baseUrl}/sizing/backtest-capital`, request);
  }

  startLive(
    expiryDate: string,
    hedgeDistance: number,
    lots: number,
    lotSize: number,
    executeBrokerOrders: boolean,
    autoLots = false,
    setupProfile = "Automatic",
    configuration = { marginHedgeEnabled: false, protectionHedgeEnabled: true, sellStrikeMode: "Delta50" },
  ): Observable<any> {
    let params = new HttpParams()
      .set("hedgeDistance", hedgeDistance)
      .set("lots", lots)
      .set("lotSize", lotSize)
      .set("executeBrokerOrders", executeBrokerOrders)
      .set("autoLots", autoLots)
      .set("setupProfile", setupProfile)
      .set("marginHedgeEnabled", configuration.marginHedgeEnabled)
      .set("protectionHedgeEnabled", configuration.protectionHedgeEnabled)
      .set("sellStrikeMode", configuration.sellStrikeMode);

    if (expiryDate) {
      params = params.set("expiryDate", expiryDate);
    }

    return this.http.post(`${this.baseUrl}/live/start`, null, { params });
  }

  stopLive(): Observable<any> {
    return this.http.post(`${this.baseUrl}/live/stop`, null);
  }

  getLiveStatus(): Observable<LiveStatusResponse> {
    return this.http.get<LiveStatusResponse>(`${this.baseUrl}/live/status`);
  }
  getBrokerDashboard(): Observable<BrokerDashboard> {
    return this.http.get<BrokerDashboard>(`${this.baseUrl}/broker/dashboard`);
  }

  placeManualBrokerOrder(request: {
    expiryDate: string;
    strike: number;
    optionType: string;
    side: string;
    lots: number;
    lotSize: number;
    orderType: string;
    limitPrice?: number;
  }): Observable<BrokerOrderResult> {
    return this.http.post<BrokerOrderResult>(
      `${this.baseUrl}/broker/manual-order`,
      request,
    );
  }

  exitBrokerPosition(request: {
    tradingSymbol: string;
    orderType: string;
    limitPrice?: number;
  }): Observable<BrokerOrderResult> {
    return this.http.post<BrokerOrderResult>(
      `${this.baseUrl}/broker/exit-position`,
      request,
    );
  }
}
