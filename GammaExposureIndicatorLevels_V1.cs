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
using NinjaTrader.Gui.Tools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class GammaExposureIndicatorLevels_V1 : Indicator
	{
		private class GammaLevel
		{
			public double NDXPrice { get; set; }
			public double NQPrice { get; set; }
			public string Name { get; set; }
		}

		private List<GammaLevel> levels = new List<GammaLevel>();
		private bool levelsLoaded = false;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description					= "Lee niveles de Gamma desde un archivo CSV y los proyecta en el gráfico (V1).";
				Name						= "GammaExposureIndicatorLevels_V1";
				Calculate					= Calculate.OnEachTick;
				IsOverlay					= true;
				DisplayInDataBox			= true;
				DrawOnPricePanel			= true;
				DrawHorizontalGridLines		= false;
				DrawVerticalGridLines		= false;
				PaintPriceMarkers			= true;
				ScaleJustification			= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive	= true;

				CsvPath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "GammaLevels.csv");

				// Visual Defaults
				LineColor 			= Brushes.Gray;
				LineStyle 			= DashStyleHelper.Dash;
				LineWidth 			= 1;
				LabelColor 			= Brushes.Yellow;
				LabelFont 			= new SimpleFont("Arial", 12);
			}
			else if (State == State.DataLoaded)
			{
				LoadLevelsFromCsv();
			}
		}

		private void LoadLevelsFromCsv()
		{
			levels.Clear();
			if (!File.Exists(CsvPath))
			{
				Print("CSV File not found: " + CsvPath);
				return;
			}

			try
			{
				string[] lines = File.ReadAllLines(CsvPath);
				// Saltar el header si existe (NDX,/NQ,Level ID)
				for (int i = 0; i < lines.Length; i++)
				{
					string line = lines[i].Trim();
					if (string.IsNullOrEmpty(line)) continue;
					
					// Intentar saltar el header si detectamos letras en los campos de precio
					if (i == 0 && (line.Contains("NDX") || line.Contains("NQ"))) continue;

					string[] parts = line.Split(',');
					if (parts.Length >= 3)
					{
						double ndx, nq;
						if (double.TryParse(parts[0], out ndx) && double.TryParse(parts[1], out nq))
						{
							levels.Add(new GammaLevel 
							{ 
								NDXPrice = ndx, 
								NQPrice = nq, 
								Name = parts[2].Trim() 
							});
						}
					}
				}
				levelsLoaded = true;
				Print("Loaded " + levels.Count + " levels from CSV (V1).");
			}
			catch (Exception ex)
			{
				Print("Error reading CSV: " + ex.Message);
			}
		}

		protected override void OnBarUpdate()
		{
			if (!levelsLoaded || levels.Count == 0) return;

			// Solo dibujamos en el último bar para eficiencia
			if (CurrentBar < Bars.Count - 1) return;

			string instrumentName = Instrument.MasterInstrument.Name.ToUpper();
			bool isNdx = instrumentName.Contains("NDX");
			bool isNq = instrumentName.Contains("NQ");
			
			// Diccionario para agrupar nombres por precio
			Dictionary<double, string> groupedLevels = new Dictionary<double, string>();

			foreach (var level in levels)
			{
				double price = isNdx ? level.NDXPrice : (isNq ? level.NQPrice : 0);
				if (price <= 0) continue;

				if (groupedLevels.ContainsKey(price))
					groupedLevels[price] += " / " + level.Name;
				else
					groupedLevels[price] = level.Name;
			}

			// Dibujar los niveles agrupados
			foreach (var entry in groupedLevels)
			{
				double price = entry.Key;
				string label = entry.Value;
				string tag = "LevelV1_" + price.ToString().Replace(",", ".");
				
				// Dibujar línea horizontal
				Draw.HorizontalLine(this, tag, price, LineColor, LineStyle, LineWidth);
				
				// Dibujar el texto agrupado
				Draw.Text(this, tag + "_Label", false, label, 0, price, -15, LabelColor, LabelFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 100);
			}
		}

		#region Properties
		[NinjaScriptProperty]
		[PropertyEditor("NinjaTrader.Gui.Tools.FilePathPicker", Filter="CSV Files (*.csv)|*.csv|All Files (*.*)|*.*")]
		[Display(Name="Ruta del Archivo CSV", Description="Seleccione el archivo .csv pulsando el botón de la derecha", GroupName="Parameters", Order=1)]
		public string CsvPath { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Line Width", GroupName="Visuals", Order=1)]
		public int LineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Line Style", GroupName="Visuals", Order=2)]
		public DashStyleHelper LineStyle { get; set; }

		[XmlIgnore]
		[Display(Name="Line Color", GroupName="Visuals", Order=3)]
		public Brush LineColor { get; set; }

		[Browsable(false)]
		public string LineColorS { get { return Serialize.BrushToString(LineColor); } set { LineColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name="Label Color", GroupName="Visuals", Order=4)]
		public Brush LabelColor { get; set; }

		[Browsable(false)]
		public string LabelColorS { get { return Serialize.BrushToString(LabelColor); } set { LabelColor = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[Display(Name="Label Font", GroupName="Visuals", Order=5)]
		public SimpleFont LabelFont { get; set; }
		#endregion
	}
}
