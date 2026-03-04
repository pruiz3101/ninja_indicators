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
using System.IO;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class DeltaFlip_AG : Indicator
	{
		private Series<double> barDelta;
		private Series<double> barMaxDelta;
		private Series<double> barMinDelta;
		
		private double currentDelta = 0;
		private double currentMaxDelta = 0;
		private double currentMinDelta = 0;
		private int consecutivePositive = 0;
		private int consecutiveNegative = 0;
		private string filePath;
		private bool headerWritten = false;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Identificador de reversión mediante el cruce de Delta (Delta Flip).";
				Name						= "DeltaFlip_AG";
				Calculate					= Calculate.OnBarClose; // Volvemos a OnBarClose, los ticks los manejaremos por serie paralela
				IsOverlay					= true;
				DisplayInDataBox			= true;
				DrawOnPricePanel			= true;
				DrawHorizontalGridLines		= true;
				DrawVerticalGridLines		= true;
				PaintPriceMarkers			= true;
				ScaleJustification			= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive	= true;

				MinConsecutiveBars			= 3;
				StarOffsetTicks				= 20;
				MinBarRangeTicks			= 15;
				MinDeltaThreshold			= 300; // Optimizado: Umbral de delta institucional
				MinVolumeThreshold			= 600; // Optimizado: Volumen mínimo para NQ/MNQ
				MinDeltaPercent				= 25.0; // Optimizado: 25% de desequilibrio (Alta Convicción)
				BullishColor				= Brushes.Lime;
				BearishColor				= Brushes.Red;
				
				UseHTF						= false;
				HTFInstrument				= null; // Al ser tipo Instrument, NT mostrará el selector oficial
				HTFPeriodType				= BarsPeriodType.Minute;
				HTFValue					= 2;

				ExportToCSV					= false;
				ExportFileName				= "DeltaFlip_Export.csv";
				
				UseAlerts					= true;
				AlertSound					= "DeltaFlip.wav";
			}
			else if (State == State.Configure)
			{
				// Importante: OrderFlowCumulativeDelta requiere datos de Tick para cálculos históricos precisos
				AddDataSeries(BarsPeriodType.Tick, 1);
				
				// Agregamos la serie secundaria si se usa MTF o Multi-Instrumento
				if (UseHTF)
				{
					// Si el campo HTFInstrument está vacío, usa el del gráfico primary
					if (HTFInstrument == null)
					{
						AddDataSeries(HTFPeriodType, HTFValue);
					}
					else
					{
						// Cargamos la serie de tiempo superior
						AddDataSeries(HTFInstrument.FullName, HTFPeriodType, HTFValue);
						// CRITICAL: Para que OrderFlow funcione en otro instrumento, DEBEMOS cargar también sus Ticks
						AddDataSeries(HTFInstrument.FullName, BarsPeriodType.Tick, 1);
					}
				}
			}
			else if (State == State.DataLoaded)
			{
				barDelta = new Series<double>(this);
				barMaxDelta = new Series<double>(this);
				barMinDelta = new Series<double>(this);

				Print(string.Format("DeltaFlip_AG: Inicializado en {0}. Modo HTF: {1}", 
					Instrument.FullName, UseHTF));
				
				if (UseHTF)
					Print(string.Format("DeltaFlip_AG: Objetivo HTF -> {0}.", 
						HTFInstrument != null ? HTFInstrument.FullName : Instrument.FullName));

				if (ExportToCSV)
				{
					string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
					filePath = Path.Combine(path, ExportFileName);
					headerWritten = false; 
					
					try {
						// Intentamos crear el archivo y escribir la cabecera
						File.WriteAllText(filePath, "Time;Instrument;Open;High;Low;Close;Volume;Delta;MaxDelta;MinDelta;DeltaPercent" + Environment.NewLine);
						headerWritten = true;
						Print("DeltaFlip_AG: Archivo CSV listo en -> " + filePath);
					} catch (Exception ex) {
						Print("DeltaFlip_AG ERROR: No se pudo crear el archivo. Asegúrate de que no esté abierto en Excel: " + ex.Message);
					}
				}
			}
		}

		protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
		{
			// Solo procesamos Trades (Last) para calcular el Delta
			if (marketDataUpdate.MarketDataType != MarketDataType.Last) return;

			// Determinamos si el tick es del instrumento correcto
			bool isPrimary = marketDataUpdate.Instrument.FullName == Instrument.FullName;
			bool isHTF = (HTFInstrument != null && marketDataUpdate.Instrument.FullName == HTFInstrument.FullName);
			
			if (UseHTF && !isHTF) return;
			if (!UseHTF && !isPrimary) return;

			// Cálculo de Delta según precio vs Bid/Ask en el momento del trade
			double tickDelta = 0;
			if (marketDataUpdate.Price >= marketDataUpdate.Ask)
				tickDelta = marketDataUpdate.Volume;
			else if (marketDataUpdate.Price <= marketDataUpdate.Bid)
				tickDelta = -marketDataUpdate.Volume;

			currentDelta += tickDelta;
			currentMaxDelta = Math.Max(currentMaxDelta, currentDelta);
			currentMinDelta = Math.Min(currentMinDelta, currentDelta);
		}

		protected override void OnBarUpdate()
		{
			// targetBIP es 2 si usamos HTF, 0 si es el gráfico normal
			int targetBIP = UseHTF ? 2 : 0;
			
			// Solo actuamos cuando cierra una vela de la serie objetivo
			if (BarsInProgress != targetBIP) return;
			if (CurrentBar < MinConsecutiveBars + 1) return;

			// En este punto, la vela acaba de CERRAR.
			// Los valores de currentDelta, currentMaxDelta, etc. contienen lo acumulado en esta vela.
			
			double lastDelta = currentDelta;
			double lastVolume = Volumes[0][0]; // Volumen de la vela que cierra
			double deltaPercent = lastVolume > 0 ? (Math.Abs(lastDelta) / lastVolume) * 100 : 0;
			double barRangeTicks = (High[0] - Low[0]) / Instrument.MasterInstrument.TickSize;

			// Guardamos para uso interno y visual
			barDelta[0] = lastDelta;
			barMaxDelta[0] = currentMaxDelta;
			barMinDelta[0] = currentMinDelta;

			// --- LÓGICA DE SEÑALES ---
			if (lastDelta > 0)
			{
				if (consecutiveNegative >= MinConsecutiveBars && barRangeTicks >= MinBarRangeTicks && lastDelta >= MinDeltaThreshold && lastVolume >= MinVolumeThreshold && deltaPercent >= MinDeltaPercent)
				{
					Draw.Diamond(this, "BullishFlip" + CurrentBar, true, 0, Low[0] - (TickSize * StarOffsetTicks), BullishColor);
					Print(string.Format("DeltaFlip_AG: SEÑAL + en {0}. Delta: {1}, Vol: {2}", Time[0], lastDelta, lastVolume));
					if (UseAlerts && State == State.Realtime) PlaySound(AlertSound);
				}
				consecutivePositive++;
				consecutiveNegative = 0;
			}
			else if (lastDelta < 0)
			{
				if (consecutivePositive >= MinConsecutiveBars && barRangeTicks >= MinBarRangeTicks && Math.Abs(lastDelta) >= MinDeltaThreshold && lastVolume >= MinVolumeThreshold && deltaPercent >= MinDeltaPercent)
				{
					Draw.Diamond(this, "BearishFlip" + CurrentBar, true, 0, High[0] + (TickSize * StarOffsetTicks), BearishColor);
					Print(string.Format("DeltaFlip_AG: SEÑAL - en {0}. Delta: {1}, Vol: {2}", Time[0], lastDelta, lastVolume));
					if (UseAlerts && State == State.Realtime) PlaySound(AlertSound);
				}
				consecutiveNegative++;
				consecutivePositive = 0;
			}
			else { consecutivePositive = 0; consecutiveNegative = 0; }

			// --- EXPORTACIÓN AL CSV ---
			if (ExportToCSV && headerWritten)
			{
				try {
					string line = string.Format("{0};{1};{2};{3};{4};{5};{6};{7};{8};{9};{10:F2}",
						Time[0].ToString("yyyy-MM-dd HH:mm:ss"), Instrument.FullName, Open[0], High[0], Low[0], Close[0],
						lastVolume, lastDelta, currentMaxDelta, currentMinDelta, deltaPercent);
					File.AppendAllText(filePath, line + Environment.NewLine);
				} catch { /* Error silencioso si el archivo se bloquea momentáneamente */ }
			}

			// RESET para la siguiente vela que empieza ahora
			currentDelta = 0;
			currentMaxDelta = 0;
			currentMinDelta = 0;
		}

		#region Properties
		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name="Min Consecutive Bars", Description="Mínimo de velas previas con el mismo delta antes de la reversión", Order=1, GroupName="Parameters")]
		public int MinConsecutiveBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name="Star Offset (Ticks)", Description="Distancia de la estrella desde el High/Low de la vela", Order=2, GroupName="Parameters")]
		public int StarOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name="Min Bar Range (Ticks)", Description="Rango mínimo de la vela (High-Low) en ticks para filtrar señales débiles", Order=3, GroupName="Parameters")]
		public int MinBarRangeTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, 10000)]
		[Display(Name="Min Delta Threshold", Description="Delta mínimo absoluto en la vela de señal para considerarla institucional", Order=4, GroupName="Parameters")]
		public int MinDeltaThreshold { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100000)]
		[Display(Name="Min Volume Threshold", Description="Volumen total mínimo en la vela para validar la señal", Order=5, GroupName="Parameters")]
		public int MinVolumeThreshold { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name="Min Delta %", Description="Porcentaje mínimo que debe representar el Delta sobre el Volumen total", Order=6, GroupName="Parameters")]
		public double MinDeltaPercent { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name="HTF Instrument", Description="Instrumento para el cálculo superior. Selecciónalo de la lista o déjalo vacío.", Order=1, GroupName="Multi-Timeframe")]
		public NinjaTrader.Cbi.Instrument HTFInstrument { get; set; }

		[Browsable(false)]
		public string HTFInstrumentName
		{
			get { return (HTFInstrument != null) ? HTFInstrument.FullName : string.Empty; }
			set { HTFInstrument = value != null && value.Length > 0 ? NinjaTrader.Cbi.Instrument.GetInstrument(value) : null; }
		}

		[NinjaScriptProperty]
		[Display(Name="Use Higher Timeframe", Description="Habilita el cálculo en una temporalidad distinta a la del gráfico", Order=2, GroupName="Multi-Timeframe")]
		public bool UseHTF { get; set; }

		[NinjaScriptProperty]
		[Display(Name="HTF Period Type", Description="Tipo de periodo para la serie superior", Order=2, GroupName="Multi-Timeframe")]
		public BarsPeriodType HTFPeriodType { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10000)]
		[Display(Name="HTF Value", Description="Valor del periodo para la serie superior", Order=3, GroupName="Multi-Timeframe")]
		public int HTFValue { get; set; }

		[XmlIgnore]
		[Display(Name="Bullish Signal Color", Order=3, GroupName="Aesthetics")]
		public Brush BullishColor { get; set; }

		[Browsable(false)]
		public string BullishColorS
		{
			get { return Serialize.BrushToString(BullishColor); }
			set { BullishColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name="Bearish Signal Color", Order=4, GroupName="Aesthetics")]
		public Brush BearishColor { get; set; }

		[Browsable(false)]
		public string BearishColorS
		{
			get { return Serialize.BrushToString(BearishColor); }
			set { BearishColor = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Display(Name="Export to CSV", Description="Habilita la grabación de datos en un archivo CSV para backtesting", Order=1, GroupName="Export Settings")]
		public bool ExportToCSV { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Export File Name", Description="Nombre del archivo (se guardará en Documentos)", Order=2, GroupName="Export Settings")]
		public string ExportFileName { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Use Audio Alerts", Description="Habilita el sonido cuando aparece un diamante", Order=1, GroupName="Alerts")]
		public bool UseAlerts { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Alert Sound File", Description="Ruta completa al archivo .wav", Order=2, GroupName="Alerts")]
		public string AlertSound { get; set; }
		#endregion
	}
}
