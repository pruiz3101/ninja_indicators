#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public class ORB_Strategy_AG : Strategy
	{
		private SessionIterator sessionIterator;
		private double orHigh = double.MinValue;
		private double orLow = double.MaxValue;
		private bool isRthSession = false;
		private DateTime currentSessionStart;
		private DateTime currentSessionEnd;
		private bool orMarkerDrawn = false;
		private bool entryOrderPlaced = false;
		private double lastTrailPrice = 0;

		private ADX adx;
		private EMA emaSlope;
		private EMA emaTrend;
		
		// VARIABLES PARA VWAP MANUAL
		private double cumVolumePrice = 0;
		private double cumVolume = 0;
		private double currentVwapValue = 0;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Estrategia ORB Automatizada con 6+ Filtros de Confirmación y Trailing Stop Avanzado.";
				Name						= "ORB_Strategy_AG";
				Calculate					= Calculate.OnEachTick;
				EntriesPerDirection			= 1;
				EntryHandling				= EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy = true;
				ExitOnSessionCloseSeconds	= 30;
				IsFillLimitOnTouch			= false;
				OrderFillResolution			= OrderFillResolution.Standard;
				Slippage					= 0;
				StartBehavior				= StartBehavior.WaitUntilFlat;
				TimeInForce					= TimeInForce.Gtc;
				TraceOrders					= false;
				RealtimeErrorHandling		= RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling			= StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade			= 20;

				// CONFIG ORB
				ORStartTime					= 113000; 
				ORDurationMinutes			= 15;
				
				// GESTIÓN DE RIESGO
				TradeQuantity               = 1;
				RRRatio                     = 3.0;
				InitialStopType             = ORBStopType.OppositeSide;
				TicksFixedStop              = 40;
				MaxRiskTicks                = 150; // Cap para evitar pérdidas excesivas ($300 en MNQ)
				
				// TRAILING STOP
				EnableTrailing              = true;
				TrailingActivationRR        = 1.0; // Breakeven al llegar a 1:1 RR
				TrailingStepTicks           = 10;
				Stage3ActivationRR          = 2.0; // Blindaje agresivo al llegar a 1:2 RR

				// VENTANA DE TIEMPO
				TradingEndHHMMSS            = 150000; // No entrar después de las 15:00

				// PROTECCIÓN RÁPIDA (Opcional)
				UseQuickBreakeven           = true;
				QuickBeTicks                = 40; // Mover a BE al estar +40 ticks ($80)
				
				// FILTROS (Mismos que el indicador)
				RequireRTH					= true;
				MinSessionStartTime			= 000000; 
				MaxSessionStartTime			= 235959; 
				VolMultiplier				= 1.5;
				VolLookback					= 20;
				UseVWAPFilter				= true;
				UseADXFilter				= false;
				ADXThreshold				= 20;
				UseEMASlopeFilter			= true;
				EMAPeriodSlope				= 9;
				UseCandleQualityFilter      = true;
				MinBodyRatio                = 0.6;
				UseTrendConfirmationFilter  = true;
				EMAPeriodTrend              = 21;
			}
			else if (State == State.Historical)
			{
			}
			else if (State == State.DataLoaded)
			{
				sessionIterator = new SessionIterator(Bars);
				adx = ADX(14);
				emaSlope = EMA(EMAPeriodSlope);
				emaTrend = EMA(EMAPeriodTrend);
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToTrade) return;

			// DETECTAR NUEVA SESIÓN
			if (Bars.IsFirstBarOfSession)
			{
				orHigh = double.MinValue; orLow = double.MaxValue;
				cumVolumePrice = 0; cumVolume = 0;
				sessionIterator.GetNextSession(Time[0], true);
				currentSessionStart = sessionIterator.ActualSessionBegin;
				currentSessionEnd = sessionIterator.ActualSessionEnd;
				orMarkerDrawn = false;
				entryOrderPlaced = false;
				lastTrailPrice = 0;

				int sessionHhmmss = currentSessionStart.Hour * 10000 + currentSessionStart.Minute * 100 + currentSessionStart.Second;
				isRthSession = !RequireRTH || (sessionHhmmss >= MinSessionStartTime && sessionHhmmss <= MaxSessionStartTime);
			}

			// VWAP MANUAL
			double p = (High[0] + Low[0] + Close[0]) / 3;
			cumVolumePrice += p * Volume[0];
			cumVolume += Volume[0];
			if (cumVolume > 0) currentVwapValue = cumVolumePrice / cumVolume;

			if (!isRthSession) return;

			DateTime orStart = Time[0].Date.AddHours(ORStartTime / 10000).AddMinutes((ORStartTime / 100) % 100);
			DateTime orEnd = orStart.AddMinutes(ORDurationMinutes);

			// CAPTURA RANGO
			if (Time[0] > orStart && Time[0] < orEnd)
			{
				orHigh = Math.Max(orHigh, High[0]);
				orLow = Math.Min(orLow, Low[0]);
			}

			if (orHigh == double.MinValue) return;

			// GESTIÓN DE POSICIÓN (TRAILING STOP)
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				ManageTrailingStop();
				return; // No buscamos nuevas entradas si ya estamos dentro
			}

			// LÓGICA DE ENTRADA (Breakout + 6 Filtros)
			int currentHhmmss = Time[0].Hour * 10000 + Time[0].Minute * 100 + Time[0].Second;
			
			if (Time[0] >= orEnd && !entryOrderPlaced && currentHhmmss <= TradingEndHHMMSS)
			{
				// Dibujar marca de final de rango (Visual solo)
				if (!orMarkerDrawn)
				{
					Draw.Text(this, "Star"+CurrentBar, "★", 0, orHigh + TickSize * 15, Brushes.Yellow);
					orMarkerDrawn = true;
				}

				// VERIFICAR FILTROS
				bool filtersOk = CheckFilters();

				if (filtersOk)
				{
					// BREAKOUT LONG
					if (Close[0] > orHigh && Close[1] <= orHigh)
					{
						ExecuteEntry(MarketPosition.Long);
					}
					// BREAKOUT SHORT
					else if (Close[0] < orLow && Close[1] >= orLow)
					{
						ExecuteEntry(MarketPosition.Short);
					}
				}
			}
		}

		private bool CheckFilters()
		{
			// 1. Volumen
			double sumVol = 0;
			for (int i = 1; i <= VolLookback; i++) sumVol += Volume[i];
			double avgVol = sumVol / VolLookback;
			bool volOk = Volume[0] > (avgVol * VolMultiplier);

			// 2. VWAP
			bool vwapOk = !UseVWAPFilter || (Close[0] > currentVwapValue && Position.MarketPosition == MarketPosition.Flat) || (Close[0] < currentVwapValue);
			// Nota: Ajustamos VWAP según dirección en ExecuteEntry, aquí es genérico
			
			// 3. ADX
			bool adxOk = !UseADXFilter || (adx[0] > ADXThreshold);

			// 4. EMA Slope
			bool slopeOk = !UseEMASlopeFilter || (emaSlope[0] != emaSlope[1]); // Direccional

			// 5. Candle Quality
			double bodySize = Math.Abs(Close[0] - Open[0]);
			double candleRange = High[0] - Low[0];
			bool qualityOk = !UseCandleQualityFilter || (candleRange > 0 && (bodySize / candleRange) >= MinBodyRatio);

			// 6. Trend EMA 21
			bool trendOk = !UseTrendConfirmationFilter || true; // Direccional

			return volOk && adxOk && qualityOk; // Filtros base, los direccionales se validan en el IF del breakout
		}

		private void ExecuteEntry(MarketPosition direction)
		{
			// Filtros Direccionales Finales
			bool vwapOk = !UseVWAPFilter || (direction == MarketPosition.Long ? Close[0] > currentVwapValue : Close[0] < currentVwapValue);
			bool slopeOk = !UseEMASlopeFilter || (direction == MarketPosition.Long ? emaSlope[0] > emaSlope[1] : emaSlope[0] < emaSlope[1]);
			bool trendOk = !UseTrendConfirmationFilter || (direction == MarketPosition.Long ? Close[0] > emaTrend[0] : Close[0] < emaTrend[1]);

			if (!vwapOk || !slopeOk || !trendOk) return;

			double slPrice = 0;
			double riskTicks = 0;

			if (direction == MarketPosition.Long)
			{
				slPrice = (InitialStopType == ORBStopType.OppositeSide) ? orLow : Close[0] - (TicksFixedStop * TickSize);
				
				// APLICAR CAP DE RIESGO MÁXIMO
				if (Close[0] - slPrice > MaxRiskTicks * TickSize)
					slPrice = Close[0] - (MaxRiskTicks * TickSize);

				riskTicks = (Close[0] - slPrice) / TickSize;
				
				SetStopLoss(CalculationMode.Price, slPrice);
				SetProfitTarget(CalculationMode.Ticks, riskTicks * RRRatio);
				EnterLong(TradeQuantity);
			}
			else
			{
				slPrice = (InitialStopType == ORBStopType.OppositeSide) ? orHigh : Close[0] + (TicksFixedStop * TickSize);
				
				// APLICAR CAP DE RIESGO MÁXIMO
				if (slPrice - Close[0] > MaxRiskTicks * TickSize)
					slPrice = Close[0] + (MaxRiskTicks * TickSize);

				riskTicks = (slPrice - Close[0]) / TickSize;
				
				SetStopLoss(CalculationMode.Price, slPrice);
				SetProfitTarget(CalculationMode.Ticks, riskTicks * RRRatio);
				EnterShort(TradeQuantity);
			}

			entryOrderPlaced = true;
		}

		private void ManageTrailingStop()
		{
			if (!EnableTrailing || Position.MarketPosition == MarketPosition.Flat) 
			{
				lastTrailPrice = 0;
				return;
			}

			double currentPnlTicks = 0;
			double entryPrice = Position.AveragePrice;
			
			// CÁLCULO DE RIESGO REAL (Debe coincidir con el CAP de ExecuteEntry)
			double rawRiskTicks = (InitialStopType == ORBStopType.OppositeSide) ? Math.Abs(entryPrice - (Position.MarketPosition == MarketPosition.Long ? orLow : orHigh)) / TickSize : TicksFixedStop;
			double initialRiskTicks = Math.Min(rawRiskTicks, MaxRiskTicks);
			
			if (initialRiskTicks <= 0) return;

			double newSLPrice = 0;
			string stageText = "Esperando BE (1:1)...";

			if (Position.MarketPosition == MarketPosition.Long)
			{
				currentPnlTicks = (Close[0] - entryPrice) / TickSize;
				
				// ESTADIO 0: PROTECCIÓN RÁPIDA (Fixed Ticks)
				if (UseQuickBreakeven && currentPnlTicks >= QuickBeTicks)
				{
					newSLPrice = entryPrice + (1 * TickSize);
					stageText = "SL @ QUICK BE";
				}

				// ESTADIO 1: BREAKEVEN (1.0 RR)
				if (currentPnlTicks >= initialRiskTicks * TrailingActivationRR)
				{
					newSLPrice = Math.Max(newSLPrice, entryPrice + (2 * TickSize)); 
					stageText = "SL @ BREAKEVEN (1.0 RR)";
				}

				// ESTADIO 2: LOCK 1.0 RR (al llegar a 1.5 RR)
				if (currentPnlTicks >= initialRiskTicks * 1.5)
				{
					newSLPrice = Math.Max(newSLPrice, entryPrice + (initialRiskTicks * 1.0 * TickSize));
					stageText = "SL @ PROFIT 1.0 RR";
				}

				// ESTADIO 3: AGRESIVO / EMA (2.0 RR)
				if (currentPnlTicks >= initialRiskTicks * Stage3ActivationRR)
				{
					double securePrice = entryPrice + (initialRiskTicks * 1.5 * TickSize);
					double trailEMA = emaTrend[0] - (5 * TickSize);
					newSLPrice = Math.Max(newSLPrice, Math.Max(securePrice, trailEMA));
					stageText = "SL @ AGRESIVO / EMA";
				}

				// Solo movemos el SL hacia ARRIBA
				if (newSLPrice > 0 && (lastTrailPrice == 0 || newSLPrice > lastTrailPrice))
				{
					SetStopLoss(CalculationMode.Price, newSLPrice);
					lastTrailPrice = newSLPrice;
				}
			}
			else // Short
			{
				currentPnlTicks = (entryPrice - Close[0]) / TickSize;
				
				// ESTADIO 0: PROTECCIÓN RÁPIDA (Fixed Ticks)
				if (UseQuickBreakeven && currentPnlTicks >= QuickBeTicks)
				{
					newSLPrice = entryPrice - (1 * TickSize);
					stageText = "SL @ QUICK BE";
				}

				// ESTADIO 1: BREAKEVEN (1.0 RR)
				if (currentPnlTicks >= initialRiskTicks * TrailingActivationRR)
				{
					newSLPrice = (newSLPrice == 0) ? entryPrice - (2 * TickSize) : Math.Min(newSLPrice, entryPrice - (2 * TickSize));
					stageText = "SL @ BREAKEVEN (1.0 RR)";
				}

				// ESTADIO 2: LOCK 1.0 RR (al llegar a 1.5 RR)
				if (currentPnlTicks >= initialRiskTicks * 1.5)
				{
					newSLPrice = (newSLPrice == 0) ? entryPrice - (initialRiskTicks * 1.0 * TickSize) : Math.Min(newSLPrice, entryPrice - (initialRiskTicks * 1.0 * TickSize));
					stageText = "SL @ PROFIT 1.0 RR";
				}

				// ESTADIO 3: AGRESIVO / EMA (2.0 RR)
				if (currentPnlTicks >= initialRiskTicks * Stage3ActivationRR)
				{
					double securePrice = entryPrice - (initialRiskTicks * 1.5 * TickSize);
					double trailEMA = emaTrend[0] + (5 * TickSize);
					newSLPrice = (newSLPrice == 0) ? Math.Min(securePrice, trailEMA) : Math.Min(newSLPrice, Math.Min(securePrice, trailEMA));
					stageText = "SL @ AGRESIVO / EMA";
				}

				// Solo movemos el SL hacia ABAJO
				if (newSLPrice > 0 && (lastTrailPrice == 0 || newSLPrice < lastTrailPrice))
				{
					SetStopLoss(CalculationMode.Price, newSLPrice);
					lastTrailPrice = newSLPrice;
				}
			}

			Draw.TextFixed(this, "TrailDebug", "TRAILING: " + stageText, TextPosition.BottomRight, Brushes.White, ChartControl.Properties.LabelFont, Brushes.Transparent, Brushes.Transparent, 0);
			
			// PANEL DE DIAGNÓSTICO
			string diag = string.Format(
				"--- PANEL DE CONTROL AG ---\n" +
				"Entrada: {0:F2}\n" +
				"Riesgo (Ticks): {1:F0}\n" +
				"PnL Actual (Ticks): {2:F0}\n" +
				"RR Actual: {3:F2}\n" +
				"Estado: {4}",
				entryPrice, initialRiskTicks, currentPnlTicks, (initialRiskTicks > 0 ? (currentPnlTicks / initialRiskTicks) : 0), stageText
			);
			Draw.TextFixed(this, "DiagPanel", diag, TextPosition.TopRight, Brushes.LimeGreen, ChartControl.Properties.LabelFont, Brushes.Black, Brushes.Black, 80);
		}

		#region Properties
		[NinjaScriptProperty] [Display(Name="OR Start (HHMMSS)", GroupName="1. ORB Config")] public int ORStartTime { get; set; }
		[NinjaScriptProperty] [Display(Name="OR Duration (min)", GroupName="1. ORB Config")] public int ORDurationMinutes { get; set; }
		[NinjaScriptProperty] [Range(1, 100)] [Display(Name="Quantity (Contracts)", GroupName="2. Risk Management")] public int TradeQuantity { get; set; }
		[NinjaScriptProperty] [Display(Name="Reward to Risk Ratio", GroupName="2. Risk Management")] public double RRRatio { get; set; }
		[NinjaScriptProperty] [Display(Name="Stop Loss Type", GroupName="2. Risk Management")] public ORBStopType InitialStopType { get; set; }
		[NinjaScriptProperty] [Display(Name="Fixed Stop Ticks", GroupName="2. Risk Management")] public int TicksFixedStop { get; set; }
		[NinjaScriptProperty] [Display(Name="Max Risk Ticks", GroupName="2. Risk Management")] public int MaxRiskTicks { get; set; }

		[NinjaScriptProperty] [Display(Name="Enable Trailing", GroupName="3. Trailing")] public bool EnableTrailing { get; set; }
		[NinjaScriptProperty] [Display(Name="Trailing Activation (RR)", GroupName="3. Trailing")] public double TrailingActivationRR { get; set; }
		[NinjaScriptProperty] [Display(Name="Stage 3 Activation (RR)", GroupName="3. Trailing")] public double Stage3ActivationRR { get; set; }
		[NinjaScriptProperty] [Display(Name="Trailing Step (Ticks)", GroupName="3. Trailing")] public int TrailingStepTicks { get; set; }

		[NinjaScriptProperty] [Display(Name="Use VWAP Filter", GroupName="4. Filters")] public bool UseVWAPFilter { get; set; }
		[NinjaScriptProperty] [Display(Name="Use ADX Filter", GroupName="4. Filters")] public bool UseADXFilter { get; set; }
		[NinjaScriptProperty] [Range(1, 100)] public int ADXThreshold { get; set; }
		[NinjaScriptProperty] [Display(Name="Use EMA Slope Filter", GroupName="4. Filters")] public bool UseEMASlopeFilter { get; set; }
		[NinjaScriptProperty] [Range(1, 100)] public int EMAPeriodSlope { get; set; }
		[NinjaScriptProperty] [Display(Name="Use Candle Quality Filter", GroupName="4. Filters")] public bool UseCandleQualityFilter { get; set; }
		[NinjaScriptProperty] [Range(0.1, 1.0)] public double MinBodyRatio { get; set; }
		[NinjaScriptProperty] [Display(Name="Use Trend Confirmation", GroupName="4. Filters")] public bool UseTrendConfirmationFilter { get; set; }
		[NinjaScriptProperty] [Range(1, 200)] public int EMAPeriodTrend { get; set; }

		[NinjaScriptProperty] [Display(Name="Vol Multiplier", GroupName="5. Volume")] public double VolMultiplier { get; set; }
		[NinjaScriptProperty] [Display(Name="Vol Lookback", GroupName="5. Volume")] public int VolLookback { get; set; }
		
		[NinjaScriptProperty] [Display(Name="Require RTH Filter", GroupName="6. Session")] public bool RequireRTH { get; set; }
		[NinjaScriptProperty] [Display(Name="Min Session Start", GroupName="6. Session")] public int MinSessionStartTime { get; set; }
		[NinjaScriptProperty] [Display(Name="Max Session Start", GroupName="6. Session")] public int MaxSessionStartTime { get; set; }
		[NinjaScriptProperty] [Display(Name="Trading End Time", GroupName="6. Session")] public int TradingEndHHMMSS { get; set; }

		[NinjaScriptProperty] [Display(Name="Use Quick Breakeven", GroupName="7. Quick Protect")] public bool UseQuickBreakeven { get; set; }
		[NinjaScriptProperty] [Display(Name="Quick BE Ticks", GroupName="7. Quick Protect")] public int QuickBeTicks { get; set; }
		#endregion
	}

	public enum ORBStopType
	{
		OppositeSide,
		FixedTicks
	}
}
