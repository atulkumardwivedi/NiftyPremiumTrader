import { Injectable } from "@angular/core";
import * as signalR from "@microsoft/signalr";
import {
  BrokerDashboard,
  BrokerPositionTick,
  BrokerRealtimeConnection,
} from "../models/trader.models";

@Injectable({ providedIn: "root" })
export class BrokerRealtimeService {
  private readonly hubUrl = "http://localhost:5080/hubs/broker";
  private connection?: signalR.HubConnection;

  private dashboardHandler?: (dashboard: BrokerDashboard) => void;
  private tickHandler?: (tick: BrokerPositionTick) => void;
  private connectionHandler?: (status: BrokerRealtimeConnection) => void;

  async start(
    onDashboard: (dashboard: BrokerDashboard) => void,
    onTick: (tick: BrokerPositionTick) => void,
    onConnection: (status: BrokerRealtimeConnection) => void,
  ): Promise<void> {
    this.dashboardHandler = onDashboard;
    this.tickHandler = onTick;
    this.connectionHandler = onConnection;

    if (
      this.connection?.state === signalR.HubConnectionState.Connected ||
      this.connection?.state === signalR.HubConnectionState.Connecting
    ) {
      return;
    }

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(this.hubUrl)
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    this.connection.on("brokerSnapshot", (dashboard: BrokerDashboard) => {
      this.dashboardHandler?.(dashboard);
    });

    this.connection.on("positionTick", (tick: BrokerPositionTick) => {
      this.tickHandler?.(tick);
    });

    this.connection.on(
      "brokerConnection",
      (status: BrokerRealtimeConnection) => {
        this.connectionHandler?.(status);
      },
    );

    this.connection.onreconnecting(() => {
      this.connectionHandler?.({
        connected: false,
        subscribedInstruments: 0,
        timestamp: new Date().toISOString(),
        message: "Browser is reconnecting to the API SignalR hub.",
      });
    });

    this.connection.onreconnected(() => {
      this.connectionHandler?.({
        connected: true,
        subscribedInstruments: 0,
        timestamp: new Date().toISOString(),
        message: "Browser reconnected to the API SignalR hub.",
      });
    });

    this.connection.onclose(() => {
      this.connectionHandler?.({
        connected: false,
        subscribedInstruments: 0,
        timestamp: new Date().toISOString(),
        message: "Browser SignalR connection is closed.",
      });
    });

    await this.connection.start();
  }

  async stop(): Promise<void> {
    if (!this.connection) {
      return;
    }

    await this.connection.stop();
    this.connection = undefined;
  }
}
