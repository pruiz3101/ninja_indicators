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
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class ORB_Indicator_AG : Indicator
	{
		private SessionIterator sessionIterator;
		private double orHigh = double.MinValue;
		private double orLow = double.MaxValue;
		private bool isRthSession = false;
		private DateTime currentSessionStart;
		private DateTime currentSessionEnd;
		private bool orMarkerDrawn = false;
		
		private Series<double> volSeries;
		private ADX adx;
		private EMA ema; // Filtro de pendiente responsivo
		private EMA ema2; // Filtro de tendencia medio plazo
		
		// VARIABLES PARA VWAP MANUAL
		private double cumVolumePrice = 0;
		private double cumVolume = 0;
		private double currentVwapValue = 0;
		
		// VARIABLES PARA DATA LOGGING Y OPTIMIZACIÓN
		private bool dataLoggedThisSession = false;
		private bool trackingTrade = false;
		private double tradeEntryPrice = 0;
		private int tradeDirection = 0; // 1 Long, -1 Short
		private double maxFavorableExcursion = 0;
		private double maxAdverseExcursion = 0;
		private int barsSinceEntry = 0;
		private string logPath;
		
		private struct SignalData
		{
			public DateTime Time;
			public string Direction;
			public double ORRangeTicks;
			public double VolRatio;
			public double ADX;
			public double EMASlope;
			public double VWAPDist;
			public double BodyRatio;
			public double TrendEMA;
		}
		private SignalData currentSignal;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "ORB Pro con Filtros Pro (Volumen, VWAP, ADX, EMA Slope).";
				Name						= "ORB_Indicator_AG";
				Calculate					= Calculate.OnEachTick; 
				IsOverlay					= true;
				
				// CONFIG CHILE
				ORStartTime					= 113000; 
				ORDurationMinutes			= 15;
				
				// FILTROS
				RequireRTH					= true;
				MinSessionStartTime			= 000000; 
				MaxSessionStartTime			= 235959; 
				
				// VOLUME BREAKOUT
				VolMultiplier				= 3.5;
				VolLookback					= 20;
				EnableVolDiamonds			= true;
				
				// VWAP FILTER
				UseVWAPFilter				= true;

				// ADX FILTER
				UseADXFilter				= true; 
				ADXThreshold				= 25;
				ADXPeriod					= 14;

				// EMA SLOPE FILTER
				UseEMASlopeFilter			= true;
				EMAPeriod					= 9;
				EMASlopeThreshold           = 18.0;
				
				// ESTÉTICA
				ORHighColor					= Brushes.Lime;
				ORLowColor					= Brushes.Red;
				LineWidth					= 2;
				ShowTextStats				= true;

				// NUEVAS SEÑALES VISUALES
				EnableOREndMarker           = true;
				OREndMarkerColor            = Brushes.Yellow;
				EnableORCountdown           = true;
				ORCountdownColor            = Brushes.Cyan;

				// FILTROS DE CONFIRMACIÓN DE BREAKOUT
				UseCandleQualityFilter      = true;
				MinBodyRatio                = 0.6;
				UseTrendConfirmationFilter  = true;
				EMAPeriod2                  = 21;
				
				// OPTIMIZACIÓN
				EnableDataLog               = false;
				LogFileName                 = "ORB_Data_Log";
			}
			else if (State == State.DataLoaded)
			{
				sessionIterator = new SessionIterator(Bars);
				volSeries = new Series<double>(this);
				adx = ADX(ADXPeriod);
				ema = EMA(EMAPeriod);
				ema2 = EMA(EMAPeriod2);
				logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), LogFileName + ".csv");
				
				if (EnableDataLog && !File.Exists(logPath))
				{
					File.WriteAllText(logPath, "Time;Direction;OR_Ticks;VolRatio;ADX;EMASlope;VWAPDist;BodyRatio;TrendEMA;MFE_Ticks;MAE_Ticks;Result\n");
				}
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < Math.Max(VolLookback, Math.Max(ADXPeriod, EMAPeriod))) return;
			
			volSeries[0] = (double)Volume[0];
			
			// SEGUIMIENTO DE TRADE ACTIVO (Para MFE/MAE)
			if (trackingTrade)
			{
				barsSinceEntry++;
				double currentProfit = 0;
				double currentLoss = 0;
				
				if (tradeDirection == 1) // Long
				{
					currentProfit = High[0] - tradeEntryPrice;
					currentLoss = tradeEntryPrice - Low[0];
				}
				else // Short
				{
					currentProfit = tradeEntryPrice - Low[0];
					currentLoss = High[0] - tradeEntryPrice;
				}
				
				maxFavorableExcursion = Math.Max(maxFavorableExcursion, currentProfit);
				maxAdverseExcursion = Math.Max(maxAdverseExcursion, currentLoss);
				
				// Cerrar tracking al final de sesión o tras X barras (ej 50 barras = ~4 horas en 5min)
				if (Bars.IsLastBarOfSession || barsSinceEntry >= 50)
				{
					LogTradeResult();
					trackingTrade = false;
				}
			}

			// DETECTAR NUEVA SESIÓN Y RESETEAR ACUMULADORES VWAP
			if (Bars.IsFirstBarOfSession)
			{
				orHigh = double.MinValue; orLow = double.MaxValue;
				cumVolumePrice = 0;
				cumVolume = 0;
				
				sessionIterator.GetNextSession(Time[0], true);
				currentSessionStart = sessionIterator.ActualSessionBegin;
				currentSessionEnd = sessionIterator.ActualSessionEnd;
				orMarkerDrawn = false;

				int sessionHhmmss = currentSessionStart.Hour * 10000 + currentSessionStart.Minute * 100 + currentSessionStart.Second;
				
				if (RequireRTH)
					isRthSession = (sessionHhmmss >= MinSessionStartTime && sessionHhmmss <= MaxSessionStartTime);
				else 
					isRthSession = true;
					
				dataLoggedThisSession = false;
			}

			// CÁLCULO VWAP MANUAL
			double price = (High[0] + Low[0] + Close[0]) / 3;
			cumVolumePrice += price * Volume[0];
			cumVolume += Volume[0];
			
			if (cumVolume > 0)
				currentVwapValue = cumVolumePrice / cumVolume;

			if (!isRthSession) return;

			// CÁLCULO DE VENTANA OR RELATIVA A LA SESIÓN
			DateTime orStart = currentSessionStart.Date.AddHours(ORStartTime / 10000).AddMinutes((ORStartTime / 100) % 100);
			if (orStart < currentSessionStart) orStart = orStart.AddDays(1);
			DateTime orEnd = orStart.AddMinutes(ORDurationMinutes);

			// CAPTURA RANGO (Estrictamente después de orStart)
			if (Time[0] > orStart && Time[0] <= orEnd)
			{
				orHigh = Math.Max(orHigh, High[0]);
				orLow = Math.Min(orLow, Low[0]);

				if (EnableORCountdown)
				{
					DateTime barStartTime = (State == State.Realtime) ? DateTime.Now : Time[0].AddMinutes(-Bars.BarsPeriod.Value);
					TimeSpan remaining = orEnd - barStartTime;
					
					if (remaining.TotalSeconds > 0)
					{
						string timerText = string.Format("OR ESTABLECIENDO: {0:mm\\:ss}", remaining);
						Draw.Text(this, "ORTimer", timerText, 0, orHigh + TickSize * 5, ORCountdownColor);
					}
					else 
					{
						RemoveDrawObject("ORTimer");
					}
				}
			}
			else if (Time[0] > orEnd)
			{
				if (EnableORCountdown) RemoveDrawObject("ORTimer");
			}

			// PLOTEO Y SEÑALES
			if (orHigh != double.MinValue)
			{
				string tag = currentSessionStart.Ticks.ToString();
				
				Draw.Line(this, tag + "H", false, orStart, orHigh, currentSessionEnd, orHigh, ORHighColor, DashStyleHelper.Solid, LineWidth);
				Draw.Line(this, tag + "L", false, orStart, orLow,  currentSessionEnd, orLow,  ORLowColor,  DashStyleHelper.Solid, LineWidth);
				
				if (EnableOREndMarker && Time[0] >= orEnd && !orMarkerDrawn)
				{
					Draw.Text(this, tag + "Star", "★", 0, orHigh + TickSize * 15, OREndMarkerColor);
					orMarkerDrawn = true;
				}

				if (EnableVolDiamonds && Time[0] >= orEnd)
				{
					double sumVol = 0;
					for (int i = 1; i <= VolLookback; i++) sumVol += volSeries[i];
					double avgVol = sumVol / VolLookback;

					if (Volume[0] > (avgVol * VolMultiplier))
					{
						bool vwapFilterUp   = !UseVWAPFilter || (Close[0] > currentVwapValue);
						bool vwapFilterDown = !UseVWAPFilter || (Close[0] < currentVwapValue);
						bool adxFilter      = !UseADXFilter  || (adx[0] > ADXThreshold);
						bool emaSlopeUp     = !UseEMASlopeFilter || ((ema[0] - ema[1]) > EMASlopeThreshold);
						bool emaSlopeDown   = !UseEMASlopeFilter || ((ema[1] - ema[0]) > EMASlopeThreshold);

						double bodySize     = Math.Abs(Close[0] - Open[0]);
						double candleRange  = High[0] - Low[0];
						bool candleQuality  = !UseCandleQualityFilter || (candleRange > 0 && (bodySize / candleRange) >= MinBodyRatio);
						bool trendFilterUp  = !UseTrendConfirmationFilter || (Close[0] > ema2[0]);
						bool trendFilterDown = !UseTrendConfirmationFilter || (Close[0] < ema2[0]);

						if (Close[0] > orHigh && Close[1] <= orHigh && vwapFilterUp && adxFilter && emaSlopeUp && candleQuality && trendFilterUp)
						{
							Draw.Diamond(this, "D"+CurrentBar, true, 0, Low[0] - TickSize*10, Brushes.Lime);
						}
						else if (Close[0] < orLow && Close[1] >= orLow && vwapFilterDown && adxFilter && emaSlopeDown && candleQuality && trendFilterDown)
						{
							Draw.Diamond(this, "D"+CurrentBar, true, 0, High[0] + TickSize*10, Brushes.Red);
						}
						
						// LOGICA DE CAPTURA DE DATOS PARA OPTIMIZACIÓN
						if (EnableDataLog && !dataLoggedThisSession && ((Close[0] > orHigh && Close[1] <= orHigh) || (Close[0] < orLow && Close[1] >= orLow)))
						{
							currentSignal = new SignalData
							{
								Time = Time[0],
								Direction = Close[0] > orHigh ? "Long" : "Short",
								ORRangeTicks = (orHigh - orLow) / TickSize,
								VolRatio = Volume[0] / avgVol,
								ADX = adx[0],
								EMASlope = ema[0] - ema[1],
								VWAPDist = (Close[0] - currentVwapValue) / TickSize,
								BodyRatio = candleRange > 0 ? (bodySize / candleRange) : 0,
								TrendEMA = (Close[0] - ema2[0]) / TickSize
							};
							
							trackingTrade = true;
							tradeEntryPrice = Close[0];
							tradeDirection = Close[0] > orHigh ? 1 : -1;
							maxFavorableExcursion = 0;
							maxAdverseExcursion = 0;
							barsSinceEntry = 0;
							dataLoggedThisSession = true;
						}
					}
				}

				if (ShowTextStats && Time[0] >= orEnd)
				{
					double ticks = (orHigh - orLow) / TickSize;
					Draw.TextFixed(this, "Stats", "ORB: " + ticks.ToString("F0") + " ticks", TextPosition.TopLeft, Brushes.White, ChartControl.Properties.LabelFont, Brushes.Transparent, Brushes.Transparent, 0);
				}
			}
		}

		private void LogTradeResult()
		{
			if (!EnableDataLog) return;
			
			string result = maxFavorableExcursion > (currentSignal.ORRangeTicks * TickSize * 2) ? "Win" : "Loss";
			
			string line = string.Format("{0};{1};{2:F0};{3:F2};{4:F2};{5:F4};{6:F0};{7:F2};{8:F0};{9:F0};{10:F0};{11}\n",
				currentSignal.Time,
				currentSignal.Direction,
				currentSignal.ORRangeTicks,
				currentSignal.VolRatio,
				currentSignal.ADX,
				currentSignal.EMASlope,
				currentSignal.VWAPDist,
				currentSignal.BodyRatio,
				currentSignal.TrendEMA,
				maxFavorableExcursion / TickSize,
				maxAdverseExcursion / TickSize,
				result
			);
			
			try {
				File.AppendAllText(logPath, line);
			} catch {
				// Silently fail if file is locked
			}
		}

		#region Properties
		[NinjaScriptProperty] [Display(Name="OR Start (HHMMSS)", GroupName="1. Config")] public int ORStartTime { get; set; }
		[NinjaScriptProperty] [Display(Name="OR Duration (min)", GroupName="1. Config")] public int ORDurationMinutes { get; set; }
		
		[NinjaScriptProperty] [Display(Name="Use VWAP Filter", GroupName="2. Filters")] public bool UseVWAPFilter { get; set; }
		[NinjaScriptProperty] [Display(Name="Use ADX Filter", GroupName="2. Filters")] public bool UseADXFilter { get; set; }
		[NinjaScriptProperty] [Display(Name="Use EMA Slope Filter", GroupName="2. Filters")] public bool UseEMASlopeFilter { get; set; }
		[NinjaScriptProperty] [Range(1, 100)] [Display(Name="EMAPeriod (Slope)", GroupName="2. Filters")] public int EMAPeriod { get; set; }
		[NinjaScriptProperty] [Range(0, 100)] [Display(Name="EMA Slope Threshold", GroupName="2. Filters")] public double EMASlopeThreshold { get; set; }
		[NinjaScriptProperty] [Range(1, 100)] [Display(Name="ADX Threshold", GroupName="2. Filters")] public int ADXThreshold { get; set; }
		[NinjaScriptProperty] [Range(1, 50)] [Display(Name="ADX Period", GroupName="2. Filters")] public int ADXPeriod { get; set; }

		[NinjaScriptProperty] [Display(Name="Enable Diamonds", GroupName="3. Volume")] public bool EnableVolDiamonds { get; set; }
		[NinjaScriptProperty] [Range(0.1, 10.0)] [Display(Name="Vol Multiplier", GroupName="3. Volume")] public double VolMultiplier { get; set; }
		[NinjaScriptProperty] [Range(5, 100)] [Display(Name="Vol Lookback (bars)", GroupName="3. Volume")] public int VolLookback { get; set; }
		
		[NinjaScriptProperty] [Display(Name="Require RTH Filter", GroupName="4. Session")] public bool RequireRTH { get; set; }
		[NinjaScriptProperty] [Display(Name="Min Session Start", GroupName="4. Session")] public int MinSessionStartTime { get; set; }
		[NinjaScriptProperty] [Display(Name="Max Session Start", GroupName="4. Session")] public int MaxSessionStartTime { get; set; }
		
		[XmlIgnore] [Display(Name="Color High", GroupName="5. Aesthetics")] public Brush ORHighColor { get; set; }
		[Browsable(false)] public string ORHighColorS { get { return Serialize.BrushToString(ORHighColor); } set { ORHighColor = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty] [Display(Name="Color Low", GroupName="5. Aesthetics")] public Brush ORLowColor { get; set; }
		[Browsable(false)] public string ORLowColorS { get { return Serialize.BrushToString(ORLowColor); } set { ORLowColor = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty] [Display(Name="Line Width", GroupName="5. Aesthetics")] public int LineWidth { get; set; }
		[NinjaScriptProperty] [Display(Name="Show Stats", GroupName="5. Aesthetics")] public bool ShowTextStats { get; set; }

		[NinjaScriptProperty] [Display(Name="Enable OR End Marker", GroupName="6. Visual Alerts")] public bool EnableOREndMarker { get; set; }
		[XmlIgnore] [Display(Name="OR End Marker Color", GroupName="6. Visual Alerts")] public Brush OREndMarkerColor { get; set; }
		[Browsable(false)] public string OREndMarkerColorS { get { return Serialize.BrushToString(OREndMarkerColor); } set { OREndMarkerColor = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty] [Display(Name="Enable OR Countdown", GroupName="6. Visual Alerts")] public bool EnableORCountdown { get; set; }
		[XmlIgnore] [Display(Name="OR Countdown Color", GroupName="6. Visual Alerts")] public Brush ORCountdownColor { get; set; }
		[Browsable(false)] public string ORCountdownColorS { get { return Serialize.BrushToString(ORCountdownColor); } set { ORCountdownColor = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty] [Display(Name="Use Candle Quality Filter", GroupName="7. Breakout Confirmation")] public bool UseCandleQualityFilter { get; set; }
		[NinjaScriptProperty] [Range(0.1, 1.0)] [Display(Name="Min Body Ratio", GroupName="7. Breakout Confirmation")] public double MinBodyRatio { get; set; }
		[NinjaScriptProperty] [Display(Name="Use Trend Confirmation (EMA 21)", GroupName="7. Breakout Confirmation")] public bool UseTrendConfirmationFilter { get; set; }
		[NinjaScriptProperty] [Range(1, 200)] [Display(Name="EMA Period 2", GroupName="7. Breakout Confirmation")] public int EMAPeriod2 { get; set; }
		
		[NinjaScriptProperty] [Display(Name="Enable Data Log", GroupName="8. Optimization")] public bool EnableDataLog { get; set; }
		[NinjaScriptProperty] [Display(Name="Log FileName", GroupName="8. Optimization")] public string LogFileName { get; set; }
		#endregion
	}
}
