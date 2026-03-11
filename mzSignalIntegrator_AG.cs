#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using System.IO;
#endregion|

namespace NinjaTrader.NinjaScript.Indicators
{
	public class mzSignalIntegrator_AG : Indicator
	{
		private dynamic bigTrade;
		private dynamic deltaDiv;
		private EMA trendEMA;
		private double prevInstVol = 0;
		private double lastFinalVol = 0;
		
		[Range(50, 5000)]
		[NinjaScriptProperty]
		[Display(Name="Mín. Volumen Ballena", Description="Volumen mínimo para detectar mano fuerte", Order=1, GroupName="Parámetros")]
		public int MinBigVolume { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Barras de Cool-off", Description="Número de barras de espera entre señales", Order=2, GroupName="Parámetros")]
		public int SignalCooloffBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Confirmación por Vela", Description="Solo dispara si la vela cierra a favor del movimiento", Order=3, GroupName="Parámetros")]
		public bool UseCandleConfirmation { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Filtro por Tendencia", Description="Solo señales a favor de la EMA", Order=4, GroupName="Parámetros")]
		public bool UseTrendFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Periodo EMA Tendencia", Description="Periodo de la EMA para filtrar por tendencia", Order=5, GroupName="Parámetros")]
		public int EMAPeriod { get; set; }

		[Range(0, 2)]
		[NinjaScriptProperty]
		[Display(Name="Nivel de Intensidad", Description="0=Cirujano (Mucha precisión), 1=Equilibrado, 2=Relajado (Más señales)", Order=6, GroupName="Parámetros")]
		public int IntensityLevel { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Exportar Datos CSV", Description="Activa la descarga de datos para optimización", Order=7, GroupName="Parámetros")]
		public bool ExportData { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Modo Diagnóstico", Description="Imprime valores en el Output para depuración", Order=8, GroupName="Parámetros")]
		public bool DiagnosticMode { get; set; }

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Integrador de Señales MZPack - Versión PRO V4.8 (Memory-Fix & Recon)";
				Name = "MZPack Pro Integrator";
				Calculate = Calculate.OnEachTick;
				IsOverlay = true;
				MinBigVolume = 120;
				SignalCooloffBars = 8;
				EMAPeriod = 50;
				IntensityLevel = 1; // Balanced
				UseCandleConfirmation = true;
				UseTrendFilter = true;
				ExportData = false;
				DiagnosticMode = true; // Habilitar por defecto para ver si conecta
				IsSuspendedWhileInactive = true;
			}
			else if (State == State.DataLoaded)
			{
				trendEMA = EMA(EMAPeriod);
				if (ExportData) InitializeLog();
			}
		}

		private bool isInitialized = false;
		private int lastSignalBar = -1;

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 20) return;

			// DESCUBRIMIENTO ÚNICO (Modo Silencioso)
			if (!isInitialized)
			{
				if (ChartControl != null)
				{
					foreach (dynamic ind in ChartControl.Indicators)
					{
						string typeName = ind.GetType().Name.ToLower();
						if (typeName.Contains("mzbigtrade")) bigTrade = ind;
						if (typeName.Contains("mzdeltadivergence")) deltaDiv = ind;
					}
					if (bigTrade != null && deltaDiv != null) {
						isInitialized = true;
						if (DiagnosticMode) Print("V4.4 | Conexión Exitosa con MZPack: BigTrade y DeltaDivergence encontrados.");
					}
				}
				if (!isInitialized) return; 
			}

			try
			{
				if (CurrentBar < 30) return; 
				if (CurrentBar - lastSignalBar < SignalCooloffBars) return;

				// 0. ACTUALIZAR MEMORIA AL CAMBIO DE VELA (Sin usar [1] para evitar barsAgo error)
				if (IsFirstTickOfBar) {
					prevInstVol = lastFinalVol;
				}

				// 1. EXTRAER VOLUMEN (Actual)
				double instVol = 0;
				PropertyInfo volProp = bigTrade.GetType().GetProperty("Volume");
				if (volProp != null) {
					var listV = volProp.GetValue(bigTrade) as ISeries<double>;
					if (listV != null && listV.Count > 0) instVol = listV[0];
				}

				// 2. ESCANEO PROFUNDO DE DIVERGENCIA (Plots 0-4)
				double bullDiv = 0, bearDiv = 0;
				for (int i = 0; i < deltaDiv.Values.Length; i++) {
					var series = deltaDiv.Values[i];
					if (series != null && series.Count > 0) {
						double val = series[0];
						if (val != 0 && val != 396) {
							if (val > 0) bullDiv = Math.Max(bullDiv, val);
							else bearDiv = Math.Abs(val) > bearDiv ? Math.Abs(val) : bearDiv;
						}
					}
				}

				// 3. TENDENCIA
				bool isUpTrend = false;
				bool isDownTrend = false;
				if (trendEMA.Count >= 2) { // Ensure there are at least two values for comparison
					isUpTrend = trendEMA[0] > trendEMA[1];
					isDownTrend = trendEMA[0] < trendEMA[1];
				}

				// 4. THRESHOLDS
				double vLimit = MinBigVolume;
				double dLimit = 15; 
				if (IntensityLevel == 0) { vLimit = MinBigVolume * 1.5; dLimit = 80; }
				if (IntensityLevel == 2) { vLimit = MinBigVolume * 0.7; dLimit = 1; }

				// 5. CONFLUENCIA (Usando prevInstVol para evitar barsAgo[1] error)
				bool whalePresent = (instVol >= vLimit || prevInstVol >= vLimit);
				bool signalTriggered = false;

				if (whalePresent)
				{
					if (bullDiv >= dLimit)
					{
						if (UseTrendFilter && !isUpTrend) goto skipSignal;
						if (UseCandleConfirmation && Close[0] <= Open[0]) {
							if (!IsFirstTickOfBar) goto skipSignal; 
						}

						Draw.ArrowUp(this, "Buy" + CurrentBar, true, 0, Low[0] - TickSize*250, Brushes.Lime);
						if (IsFirstTickOfBar) Alert("Buy", Priority.High, "KEY SPOT: COMPRA", "bigtrade.wav", 10, Brushes.Black, Brushes.Lime);
						lastSignalBar = CurrentBar;
						signalTriggered = true;
					}
					else if (bearDiv >= dLimit)
					{
						if (UseTrendFilter && !isDownTrend) goto skipSignal;
						if (UseCandleConfirmation && Close[0] >= Open[0]) {
							if (!IsFirstTickOfBar) goto skipSignal;
						}

						Draw.ArrowDown(this, "Sell" + CurrentBar, true, 0, High[0] + TickSize*250, Brushes.Red);
						if (IsFirstTickOfBar) Alert("Sell", Priority.High, "KEY SPOT: VENTA", "bigtrade.wav", 10, Brushes.Black, Brushes.Red);
						lastSignalBar = CurrentBar;
						signalTriggered = true;
					}
				}

				skipSignal:
				if (DiagnosticMode && (instVol > 0 || bullDiv > 0 || bearDiv > 0)) {
					Print(string.Format("V4.7 | Bar:{0} | Vol:{1}(P:{2}) | Div:Bt{3} Br{4} | Sig:{5}", 
						CurrentBar, instVol, prevInstVol, bullDiv, bearDiv, signalTriggered));
				}

				if (IsFirstTickOfBar) ExportToCSV(instVol, bullDiv, bearDiv, isUpTrend, signalTriggered);

				// GUARDAR EL ÚLTIMO ESTADO DEL VOLUMEN (Tracking continuo)
				lastFinalVol = instVol;
			}
			catch (Exception ex) { if (DiagnosticMode) Print("Error V4.8: " + ex.Message); }
		}

		private string logFilePath;
		private void InitializeLog()
		{
			string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "bin", "Custom", "MZPack_Data");
			if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
			logFilePath = Path.Combine(folder, "MZPack_Optimization_" + Instrument.FullName.Replace(" ", "_") + ".csv");
			
			if (!File.Exists(logFilePath))
			{
				File.WriteAllText(logFilePath, "Time;Bar;Price;InstVol;BullDiv;BearDiv;TrendUp;Signal\n");
			}
		}

		private void ExportToCSV(double vol, double bull, double bear, bool trend, bool signal)
		{
			try {
				string line = string.Format("{0};{1};{2};{3};{4};{5};{6};{7}\n",
					Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
					CurrentBar,
					Close[0],
					vol,
					bull,
					bear,
					trend ? 1 : 0,
					signal ? 1 : 0);
				File.AppendAllText(logFilePath, line);
			} catch { }
		}
	}
}
