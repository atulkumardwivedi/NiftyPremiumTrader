import { CommonModule } from "@angular/common";
import {
  AfterViewInit,
  Component,
  ElementRef,
  HostListener,
  Input,
  OnChanges,
  SimpleChanges,
  ViewChild,
} from "@angular/core";
import {
  OptionCandle,
  PremiumSignal,
  StrategyTrade,
} from "../../models/trader.models";

@Component({
  selector: "app-premium-chart",
  standalone: true,
  imports: [CommonModule],
  template: `
    <section class="chart-card">
      <div class="chart-title">{{ title }}</div>
      <canvas #chartCanvas></canvas>
      <div class="legend">
        <span><i class="up"></i> Up candle</span>
        <span><i class="down"></i> Down candle</span>
        <span><i class="signal-dot"></i> Signal</span>
        <span><i class="entry"></i> Entry</span>
        <span><i class="hedge"></i> Hedge</span>
        <span><i class="exit"></i> Exit</span>
      </div>
    </section>
  `,
  styles: [
    `
      .chart-card {
        background: white;
        border: 1px solid #d8dee8;
        border-radius: 8px;
        margin-bottom: 16px;
        padding: 12px;
      }

      .chart-title {
        font-weight: 700;
        margin-bottom: 8px;
      }

      canvas {
        width: 100%;
        height: 430px;
        display: block;
      }

      .legend {
        display: flex;
        flex-wrap: wrap;
        gap: 14px;
        margin-top: 8px;
        font-size: 12px;
        color: #475467;
      }

      .legend span {
        display: flex;
        align-items: center;
        gap: 5px;
      }

      .legend i {
        width: 10px;
        height: 10px;
        display: inline-block;
        border-radius: 2px;
      }

      .up {
        background: #31a354;
      }
      .down {
        background: #e25555;
      }
      .signal-dot {
        background: #0f766e;
        border-radius: 50% !important;
      }
      .entry {
        background: #2563eb;
        border-radius: 50% !important;
      }
      .hedge {
        background: #ea8c00;
        border-radius: 50% !important;
      }
      .exit {
        background: #7c3aed;
        border-radius: 50% !important;
      }
    `,
  ],
})
export class PremiumChartComponent implements AfterViewInit, OnChanges {
  @ViewChild("chartCanvas")
  canvasRef?: ElementRef<HTMLCanvasElement>;

  @Input() candles: OptionCandle[] = [];
  @Input() trades: StrategyTrade[] = [];
  @Input() signal?: PremiumSignal;
  @Input() visibleUntil?: string;
  @Input() title = "Premium candlestick chart";

  ngAfterViewInit(): void {
    this.draw();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (
      changes["candles"] ||
      changes["trades"] ||
      changes["signal"] ||
      changes["visibleUntil"]
    ) {
      setTimeout(() => this.draw());
    }
  }

  @HostListener("window:resize")
  onResize(): void {
    this.draw();
  }

  private draw(): void {
    const canvas = this.canvasRef?.nativeElement;

    if (!canvas) {
      return;
    }

    const width = canvas.clientWidth || 1000;
    const height = 430;
    const pixelRatio = window.devicePixelRatio || 1;

    canvas.width = Math.floor(width * pixelRatio);
    canvas.height = Math.floor(height * pixelRatio);

    const context = canvas.getContext("2d");

    if (!context) {
      return;
    }

    context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);

    context.clearRect(0, 0, width, height);

    const visible = this.getVisibleCandles();

    if (visible.length === 0) {
      this.drawMessage(context, width, height, "No premium candles available");

      return;
    }

    const left = 62;
    const right = 18;
    const top = 18;
    const bottom = 44;
    const chartWidth = width - left - right;
    const chartHeight = height - top - bottom;

    let minPrice = Math.min(...visible.map((x) => x.low));
    let maxPrice = Math.max(...visible.map((x) => x.high));

    if (minPrice === maxPrice) {
      minPrice -= 1;
      maxPrice += 1;
    }

    const pricePadding = (maxPrice - minPrice) * 0.08;
    minPrice -= pricePadding;
    maxPrice += pricePadding;

    this.drawGrid(
      context,
      left,
      top,
      chartWidth,
      chartHeight,
      minPrice,
      maxPrice,
    );

    const slotWidth = chartWidth / visible.length;
    const bodyWidth = Math.max(3, Math.min(10, slotWidth * 0.55));

    for (let index = 0; index < visible.length; index++) {
      const candle = visible[index];
      const centerX = left + index * slotWidth + slotWidth / 2;

      const openY = this.priceToY(
        candle.open,
        minPrice,
        maxPrice,
        top,
        chartHeight,
      );

      const closeY = this.priceToY(
        candle.close,
        minPrice,
        maxPrice,
        top,
        chartHeight,
      );

      const highY = this.priceToY(
        candle.high,
        minPrice,
        maxPrice,
        top,
        chartHeight,
      );

      const lowY = this.priceToY(
        candle.low,
        minPrice,
        maxPrice,
        top,
        chartHeight,
      );

      const isUp = candle.close >= candle.open;

      context.strokeStyle = isUp ? "#16803c" : "#c53030";
      context.fillStyle = isUp ? "#31a354" : "#e25555";
      context.lineWidth = 1;

      context.beginPath();
      context.moveTo(centerX, highY);
      context.lineTo(centerX, lowY);
      context.stroke();

      const bodyTop = Math.min(openY, closeY);
      const bodyHeight = Math.max(1, Math.abs(closeY - openY));

      context.fillRect(centerX - bodyWidth / 2, bodyTop, bodyWidth, bodyHeight);

      const labelEvery = Math.max(1, Math.ceil(visible.length / 10));

      if (index % labelEvery === 0) {
        const indiaTime = this.indiaTime(candle.candleTime);

        context.fillStyle = "#475467";
        context.font = "11px Arial";
        context.textAlign = "center";
        context.fillText(indiaTime, centerX, top + chartHeight + 20);
      }
    }

    this.drawTradeMarkers(
      context,
      visible,
      left,
      top,
      chartHeight,
      slotWidth,
      minPrice,
      maxPrice,
    );

    this.drawSignalMarker(
      context,
      visible,
      left,
      top,
      chartHeight,
      slotWidth,
      minPrice,
      maxPrice,
    );
  }

  private getVisibleCandles(): OptionCandle[] {
    if (!this.visibleUntil) {
      return this.candles;
    }

    const visibleEnd = new Date(this.visibleUntil).getTime();

    return this.candles.filter((candle) => {
      return new Date(candle.candleTime).getTime() <= visibleEnd;
    });
  }

  private drawGrid(
    context: CanvasRenderingContext2D,
    left: number,
    top: number,
    width: number,
    height: number,
    minPrice: number,
    maxPrice: number,
  ): void {
    context.strokeStyle = "#e5e7eb";
    context.lineWidth = 1;
    context.font = "11px Arial";
    context.fillStyle = "#475467";
    context.textAlign = "right";

    const lines = 6;

    for (let index = 0; index <= lines; index++) {
      const ratio = index / lines;
      const y = top + ratio * height;
      const price = maxPrice - ratio * (maxPrice - minPrice);

      context.beginPath();
      context.moveTo(left, y);
      context.lineTo(left + width, y);
      context.stroke();

      context.fillText(price.toFixed(2), left - 8, y + 4);
    }
  }

  private drawSignalMarker(
    context: CanvasRenderingContext2D,
    candles: OptionCandle[],
    left: number,
    top: number,
    height: number,
    slotWidth: number,
    minPrice: number,
    maxPrice: number,
  ): void {
    if (!this.signal || this.signal.direction === 0) {
      return;
    }

    const signalTime = new Date(this.signal.candleTime).getTime();
    const index = candles.findIndex((candle) => {
      return new Date(candle.candleTime).getTime() === signalTime;
    });

    if (index < 0) {
      return;
    }

    const candle = candles[index];
    const x = left + index * slotWidth + slotWidth / 2;
    const y = this.priceToY(candle.close, minPrice, maxPrice, top, height);

    const label = this.signal.direction === 1 ? "BULL SIGNAL" : "BEAR SIGNAL";

    context.fillStyle = "#0f766e";
    context.beginPath();
    context.arc(x, y, 5, 0, Math.PI * 2);
    context.fill();

    context.font = "10px Arial";
    context.textAlign = "left";
    context.fillText(label, x + 7, y + 13);
  }

  private drawTradeMarkers(
    context: CanvasRenderingContext2D,
    candles: OptionCandle[],
    left: number,
    top: number,
    height: number,
    slotWidth: number,
    minPrice: number,
    maxPrice: number,
  ): void {
    if (candles.length === 0) {
      return;
    }

    for (const trade of this.trades) {
      if (trade.soldStrike !== candles[0].strike) {
        continue;
      }

      if (trade.soldOptionType !== candles[0].optionType) {
        continue;
      }

      const signalPrice = this.findCloseAt(candles, trade.signalTime);

      this.drawMarker(
        context,
        candles,
        trade.signalTime,
        signalPrice,
        "SIGNAL",
        "#0f766e",
        left,
        top,
        height,
        slotWidth,
        minPrice,
        maxPrice,
      );

      this.drawMarker(
        context,
        candles,
        trade.entryTime,
        trade.entryPrice,
        "ENTRY",
        "#2563eb",
        left,
        top,
        height,
        slotWidth,
        minPrice,
        maxPrice,
      );

      if (trade.marginHedgeEntryTime) {
        this.drawMarker(context, candles, trade.marginHedgeEntryTime, this.findOpenAt(candles, trade.marginHedgeEntryTime),
          "MARGIN BUY", "#9333ea", left, top, height, slotWidth, minPrice, maxPrice);
      }
      if (trade.hedgeEntryTime) {
        const soldPriceAtHedge = this.findOpenAt(candles, trade.hedgeEntryTime);

        this.drawMarker(
          context,
          candles,
          trade.hedgeEntryTime,
          soldPriceAtHedge,
          "HEDGE",
          "#ea8c00",
          left,
          top,
          height,
          slotWidth,
          minPrice,
          maxPrice,
        );
      }

      if (trade.exitTime && trade.soldExitPrice) {
        this.drawMarker(
          context,
          candles,
          trade.exitTime,
          trade.soldExitPrice,
          "EXIT",
          "#7c3aed",
          left,
          top,
          height,
          slotWidth,
          minPrice,
          maxPrice,
        );
      }
    }
  }

  private drawMarker(
    context: CanvasRenderingContext2D,
    candles: OptionCandle[],
    markerTime: string,
    markerPrice: number,
    label: string,
    color: string,
    left: number,
    top: number,
    height: number,
    slotWidth: number,
    minPrice: number,
    maxPrice: number,
  ): void {
    const markerMs = new Date(markerTime).getTime();

    let index = candles.findIndex((candle) => {
      return new Date(candle.candleTime).getTime() === markerMs;
    });

    if (index < 0) {
      index = candles.findIndex((candle) => {
        return new Date(candle.candleTime).getTime() >= markerMs;
      });
    }

    if (index < 0) {
      return;
    }

    const x = left + index * slotWidth + slotWidth / 2;
    const y = this.priceToY(markerPrice, minPrice, maxPrice, top, height);

    context.fillStyle = color;
    context.beginPath();
    context.arc(x, y, 5, 0, Math.PI * 2);
    context.fill();

    context.font = "10px Arial";
    context.textAlign = "left";
    context.fillText(label, x + 7, y - 7);
  }

  private findCloseAt(candles: OptionCandle[], time: string): number {
    const target = new Date(time).getTime();

    const candle = candles.find((item) => {
      return new Date(item.candleTime).getTime() === target;
    });

    if (candle) {
      return candle.close;
    }

    return candles.at(-1)?.close ?? 0;
  }

  private findOpenAt(candles: OptionCandle[], time: string): number {
    const target = new Date(time).getTime();

    const candle = candles.find((item) => {
      return new Date(item.candleTime).getTime() === target;
    });

    if (candle) {
      return candle.open;
    }

    return candles.at(-1)?.close ?? 0;
  }

  private priceToY(
    price: number,
    minPrice: number,
    maxPrice: number,
    top: number,
    height: number,
  ): number {
    const ratio = (maxPrice - price) / (maxPrice - minPrice);
    return top + ratio * height;
  }

  private drawMessage(
    context: CanvasRenderingContext2D,
    width: number,
    height: number,
    message: string,
  ): void {
    context.fillStyle = "#667085";
    context.font = "14px Arial";
    context.textAlign = "center";
    context.fillText(message, width / 2, height / 2);
  }

  private indiaTime(value: string): string {
    return new Date(value).toLocaleTimeString("en-IN", {
      hour: "2-digit",
      minute: "2-digit",
      hour12: false,
      timeZone: "Asia/Kolkata",
    });
  }
}
