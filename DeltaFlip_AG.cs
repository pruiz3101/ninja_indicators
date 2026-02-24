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

namespace NinjaTrader.NinjaScript.Indicators
{
	public class DeltaFlip_AG : Indicator
	{
		private OrderFlowCumulativeDelta cumulativeDelta;
		private Series<double> barDelta;
		private int consecutivePositive = 0;
		private int consecutiveNegative = 0;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Identificador de reversión mediante el cruce de Delta (Delta Flip).";
				Name						= "DeltaFlip_AG";
				Calculate					= Calculate.OnBarClose;
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
				MinDeltaThreshold			= 250;
				BullishColor				= Brushes.Lime;
				BearishColor				= Brushes.Red;
				
				UseHTF						= false;
				HTFInstrument				= null; // Al ser tipo Instrument, NT mostrará el selector oficial
				HTFPeriodType				= BarsPeriodType.Minute;
				HTFValue					= 2;
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
				// Si no hay datos HTF, usamos la serie 0. De lo contrario la 2 (Serie 1 es Ticks)
				int htfIndex = UseHTF ? 2 : 0;
				
				// IMPORTANTE: Para OrderFlow en series secundarias, es más estable usar BarsArray[index]
				cumulativeDelta = OrderFlowCumulativeDelta(BarsArray[htfIndex], CumulativeDeltaType.BidAsk, CumulativeDeltaPeriod.Bar, 0);
				
				Print(string.Format("DeltaFlip_AG: Inicializado en {0}. Modo HTF: {1} (Index {2}).", 
					Instrument.FullName, UseHTF, htfIndex));
				
				if (UseHTF)
					Print(string.Format("DeltaFlip_AG: Objetivo HTF -> {0}.", 
						HTFInstrument != null ? HTFInstrument.FullName : Instrument.FullName));
			}
		}

		protected override void OnBarUpdate()
		{
			// Si estamos en la serie de Ticks (BIP 1), no hacemos nada
			if (BarsInProgress == 1) return;

			// Diagnostic: Imprimir cada vez que cierra una vela si estamos en debug
			// if (CurrentBar < 10) Print("BIP: " + BarsInProgress + " Bar: " + CurrentBar + " Delta: " + (cumulativeDelta != null ? cumulativeDelta.DeltaClose[0].ToString() : "null"));

			// Determinamos en qué serie debemos procesar la lógica de cálculo
			int targetBIP = UseHTF ? 2 : 0;
			
			// Solo procesamos cuando la serie objetivo (Chart o HTF) tiene una nueva vela cerrada
			if (BarsInProgress != targetBIP) return;
			
			if (CurrentBars[targetBIP] < MinConsecutiveBars + 1) return;

			// Obtenemos el delta de la vela correspondiente
			double currentDelta = cumulativeDelta.DeltaClose[0];
			
			// Diagnostic para el usuario:
			// Print(string.Format("Barra HTF #{0} cerrada. Delta: {1}", CurrentBars[targetBIP], currentDelta));

			// Rango de vela de la serie objetivo (donde calculamos)
			double barRangeTicks = (Highs[targetBIP][0] - Lows[targetBIP][0]) / BarsArray[targetBIP].Instrument.MasterInstrument.TickSize;

			// Lógica de detección de señales de reversión
			if (currentDelta > 0)
			{
				if (consecutiveNegative >= MinConsecutiveBars && barRangeTicks >= MinBarRangeTicks && currentDelta >= MinDeltaThreshold)
				{
					// Dibujamos en los Highs/Lows de la serie 0 (el gráfico que ve el usuario)
					Draw.Diamond(this, "BullishFlip" + CurrentBars[targetBIP], true, 0, Lows[0][0] - (TickSize * StarOffsetTicks), BullishColor);
					Print("DeltaFlip_AG: SEÑAL ALCISTA detectada en barra " + CurrentBars[targetBIP]);
				}
				
				consecutivePositive++;
				consecutiveNegative = 0;
			}
			else if (currentDelta < 0)
			{
				if (consecutivePositive >= MinConsecutiveBars && barRangeTicks >= MinBarRangeTicks && Math.Abs(currentDelta) >= MinDeltaThreshold)
				{
					Draw.Diamond(this, "BearishFlip" + CurrentBars[targetBIP], true, 0, Highs[0][0] + (TickSize * StarOffsetTicks), BearishColor);
					Print("DeltaFlip_AG: SEÑAL BAJISTA detectada en barra " + CurrentBars[targetBIP]);
				}
				
				consecutiveNegative++;
				consecutivePositive = 0;
			}
			else
			{
				// Si el delta es exactamente 0, reseteamos conteo
				consecutivePositive = 0;
				consecutiveNegative = 0;
			}
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
		#endregion
	}
}
