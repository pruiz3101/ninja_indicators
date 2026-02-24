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
	public class SupplyAndDemandZones : Indicator
	{
		private enum SDPattern { RBR, DBD, RBD, DBR, Unknown }

		private class SDZone
		{
			public string Tag;
			public double High;
			public double Low;
			public int StartBar;
			public bool IsSupply;
			public bool IsMitigated;
			public bool IsTested;
			public SDPattern Pattern;
			public double Strength;
			public DateTime StartTime;
		}

		private List<SDZone> zones = new List<SDZone>();
		private int zoneCounter = 0;
		private ATR atr;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Indicador PRO de Supply and Demand con detección dinámica ERC y patrones institucionales.";
				Name						= "SupplyAndDemandZones";
				Calculate					= Calculate.OnBarClose;
				IsOverlay					= true;
				
				// Parámetros Detection
				UseDynamicERC				= true;
				ERCMultiplier				= 2.0; // Incrementado para filtrar ruido
				MinStrength					= 2.0; // Nuevo filtro de fuerza
				MinMovePoints				= 100.0;
				MaxBaseBars					= 3; // Base más apretada
				MaxLegOutBars				= 3; // Movimiento más explosivo
				
				// Parámetros Estética
				SupplyColor					= Brushes.PaleVioletRed;
				DemandColor					= Brushes.LightSkyBlue;
				Opacity						= 25;
				ShowMitigated				= false;
				ShowLabels					= true;
				LabelFont					= new NinjaTrader.Gui.Tools.SimpleFont("Arial", 10);
			}
			else if (State == State.DataLoaded)
			{
				atr = ATR(14);
				zones.Clear();
				zoneCounter = 0;
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < Math.Max(14, MaxBaseBars + MaxLegOutBars + 1)) return;

			// 1. MITIGATION & TESTING LOGIC
			for (int i = zones.Count - 1; i >= 0; i--)
			{
				var zone = zones[i];
				if (zone.IsMitigated) continue;

				// Touch detection
				if (High[0] >= zone.Low && Low[0] <= zone.High)
				{
					// Si el precio CIERRA a través de la zona, es mitigación completa
					if ((zone.IsSupply && Close[0] > zone.High) || (!zone.IsSupply && Close[0] < zone.Low))
					{
						zone.IsMitigated = true;
						if (!ShowMitigated) RemoveDrawObject(zone.Tag);
						else RefreshZone(zone);
					}
					else 
					{
						// Es un testeo (solo toque)
						zone.IsTested = true;
						RefreshZone(zone);
					}
				}
				else
				{
					RefreshZone(zone);
				}
			}

			// 2. DETECTION LOGIC (ERC & Patterns)
			double threshold = UseDynamicERC ? (atr[0] * ERCMultiplier) : MinMovePoints;
			
			for (int barsBack = 0; barsBack < MaxLegOutBars; barsBack++)
			{
				double moveSize = Close[0] - Open[barsBack];
				double absoluteMove = Math.Abs(moveSize);

				if (absoluteMove >= threshold)
				{
					bool isBullishMove = moveSize > 0;
					
					// Filtro de Calidad (70% velas misma dirección)
					int matchingBars = 0;
					for (int k = 0; k <= barsBack; k++)
					{
						if (isBullishMove && Close[k] > Open[k]) matchingBars++;
						if (!isBullishMove && Close[k] < Open[k]) matchingBars++;
					}
					if (matchingBars / (double)(barsBack + 1) < 0.7) continue;

					// Identificar Patrón
					SDPattern pattern = SDPattern.Unknown;
					int baseStart = barsBack + 1;
					
					// Miramos la tendencia previa a la base (para clasificar)
					double prevTrend = Close[baseStart] - Open[baseStart + 3]; 

					if (isBullishMove) // Posible Demanda
					{
						pattern = (prevTrend > 0) ? SDPattern.RBR : SDPattern.DBR;
					}
					else // Posible Oferta
					{
						pattern = (prevTrend < 0) ? SDPattern.DBD : SDPattern.RBD;
					}

					// Definir Zona de Base
					double zoneHigh = double.MinValue;
					double zoneLow = double.MaxValue;
					for (int j = 0; j < MaxBaseBars; j++)
					{
						zoneHigh = Math.Max(zoneHigh, High[baseStart + j]);
						zoneLow = Math.Min(zoneLow, Low[baseStart + j]);
					}

					// Fuerza (basada en el ratio de salida vs ATR)
					double strength = absoluteMove / atr[0];

					if (strength < MinStrength) continue;

					CreateZone(zoneHigh, zoneLow, CurrentBar - baseStart, !isBullishMove, pattern, strength);
					break; 
				}
			}
		}

		private void CreateZone(double high, double low, int startBar, bool isSupply, SDPattern pattern, double strength)
		{
			// Evitar duplicados exactos
			if (zones.Any(z => !z.IsMitigated && Math.Abs(z.High - high) < TickSize && Math.Abs(z.Low - low) < TickSize))
				return;

			zoneCounter++;
			SDZone newZone = new SDZone
			{
				Tag = "SD_" + zoneCounter,
				High = high,
				Low = low,
				StartBar = startBar,
				IsSupply = isSupply,
				IsMitigated = false,
				IsTested = false,
				Pattern = pattern,
				Strength = strength,
				StartTime = Time[CurrentBar - startBar]
			};

			zones.Add(newZone);
			RefreshZone(newZone);
		}

		private void RefreshZone(SDZone zone)
		{
			if (zone.IsMitigated && !ShowMitigated) return;

			Brush color = zone.IsSupply ? SupplyColor : DemandColor;
			int currentOpacity = zone.IsMitigated ? 5 : (zone.IsTested ? Opacity / 2 : Opacity);

			int barsAgo = CurrentBar - zone.StartBar;
			Draw.Rectangle(this, zone.Tag, false, barsAgo, zone.High, 0, zone.Low, color, color, currentOpacity);

			if (ShowLabels)
			{
				string label = string.Format("{0} ({1:F1}) {2}", 
					zone.Pattern, 
					zone.Strength, 
					zone.IsMitigated ? "[M]" : (zone.IsTested ? "[T]" : ""));
				
				Draw.Text(this, zone.Tag + "_lbl", false, label, barsAgo, zone.IsSupply ? zone.High + TickSize*5 : zone.Low - TickSize*5, 0, Brushes.White, LabelFont, TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
			}
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name="Usar ATR Dinámico (ERC)", Order=1, GroupName="1. Detección")]
		public bool UseDynamicERC { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, 10.0)]
		[Display(Name="Fuerza Mínima (Score)", Description="Filtra zonas por su explosividad relativa", Order=2, GroupName="1. Detección")]
		public double MinStrength { get; set; }

		[NinjaScriptProperty]
		[Range(0.5, 5.0)]
		[Display(Name="Multiplicador ATR (ERC)", Order=3, GroupName="1. Detección")]
		public double ERCMultiplier { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name="Puntos Mínimos (Fallback)", Order=3, GroupName="1. Detección")]
		public double MinMovePoints { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name="Max Velas Base", Order=4, GroupName="1. Detección")]
		public int MaxBaseBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name="Max Velas Movimiento (Leg)", Order=5, GroupName="1. Detección")]
		public int MaxLegOutBars { get; set; }

		[XmlIgnore]
		[Display(Name="Color Supply", Order=6, GroupName="2. Estética")]
		public Brush SupplyColor { get; set; }
		[Browsable(false)] public string SupplyColorS { get { return Serialize.BrushToString(SupplyColor); } set { SupplyColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name="Color Demand", Order=7, GroupName="2. Estética")]
		public Brush DemandColor { get; set; }
		[Browsable(false)] public string DemandColorS { get { return Serialize.BrushToString(DemandColor); } set { DemandColor = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name="Opacidad (%)", Order=8, GroupName="2. Estética")]
		public int Opacity { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Mostrar Etiquetas", Order=9, GroupName="2. Estética")]
		public bool ShowLabels { get; set; }

		[XmlIgnore]
		[Display(Name="Fuente Etiquetas", Order=10, GroupName="2. Estética")]
		public NinjaTrader.Gui.Tools.SimpleFont LabelFont { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Mostrar Mitigadas", Order=11, GroupName="3. Comportamiento")]
		public bool ShowMitigated { get; set; }
		#endregion
	}
}
