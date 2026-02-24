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
using System.Net;
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
	public class GammaExposureIndicatorLevels : Indicator
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
				Description					= "Lee niveles de Gamma desde una URL pública y los proyecta en el gráfico.";
				Name						= "GammaExposureIndicatorLevels";
				Calculate					= Calculate.OnEachTick;
				IsOverlay					= true;
				DisplayInDataBox			= true;
				DrawOnPricePanel			= true;
				DrawHorizontalGridLines		= false;
				DrawVerticalGridLines		= false;
				PaintPriceMarkers			= true;
				ScaleJustification			= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive	= true;

				DataUrl = "https://www.dropbox.com/scl/fi/jmfdtuxpacu2wf0opb90q/niveles_gamma_actuales.csv?rlkey=zykm2dqdd5h25klh7vh6o0me6&dl=1";

				// Visual Defaults
				LineColor 			= Brushes.Gray;
				LineStyle 			= DashStyleHelper.Dash;
				LineWidth 			= 1;
				LabelColor 			= Brushes.Yellow;
				LabelFont 			= new SimpleFont("Arial", 12);
				LabelRightOffset	= 80; // Aumentado para que no choque con los precios
			}
			else if (State == State.DataLoaded)
			{
				LoadLevelsFromUrl();
			}
		}

		private void LoadLevelsFromUrl()
		{
			levels.Clear();
			if (string.IsNullOrEmpty(DataUrl))
			{
				Print("GammaIndicator: URL is empty.");
				return;
			}

			try
			{
				using (WebClient client = new WebClient())
				{
					// Forzar TLS 1.2 si es necesario por seguridad de Dropbox
					ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
					
					string csvData = client.DownloadString(DataUrl);
					if (string.IsNullOrEmpty(csvData))
					{
						Print("GammaIndicator: No data downloaded from URL.");
						return;
					}

					using (StringReader reader = new StringReader(csvData))
					{
						string line;
						int lineCount = 0;
						while ((line = reader.ReadLine()) != null)
						{
							line = line.Trim();
							if (string.IsNullOrEmpty(line)) continue;
							
							// Saltar el header
							if (lineCount == 0 && (line.Contains("NDX") || line.Contains("NQ")))
							{
								lineCount++;
								continue;
							}

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
							lineCount++;
						}
					}
					levelsLoaded = true;
					Print("Loaded " + levels.Count + " levels from URL.");
				}
			}
			catch (Exception ex)
			{
				Print("GammaIndicator: Error downloading or parsing data: " + ex.Message);
			}
		}

		protected override void OnBarUpdate()
		{
			if (!levelsLoaded || levels.Count == 0) return;

			// Solo dibujamos las líneas en el último bar para eficiencia
			if (CurrentBar < Bars.Count - 1) return;

			string instrumentName = Instrument.MasterInstrument.Name.ToUpper();
			bool isNdx = instrumentName.Contains("NDX");
			bool isNq = instrumentName.Contains("NQ");
			
			// Dibujamos las líneas horizontales
			foreach (var level in levels)
			{
				double price = isNdx ? level.NDXPrice : (isNq ? level.NQPrice : 0);
				if (price <= 0) continue;

				string tag = "Line_" + level.Name.Replace(" ", "_") + "_" + price;
				Draw.HorizontalLine(this, tag, price, LineColor, LineStyle, LineWidth);
			}
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			if (!levelsLoaded || levels.Count == 0 || LabelFont == null) return;

			string instrumentName = Instrument.MasterInstrument.Name.ToUpper();
			bool isNdx = instrumentName.Contains("NDX");
			bool isNq = instrumentName.Contains("NQ");

			// Agrupamos por precio para el renderizado
			Dictionary<double, string> groupedForRender = new Dictionary<double, string>();
			foreach (var level in levels)
			{
				double price = isNdx ? level.NDXPrice : (isNq ? level.NQPrice : 0);
				if (price <= 0) continue;

				if (groupedForRender.ContainsKey(price))
					groupedForRender[price] += " / " + level.Name;
				else
					groupedForRender[price] = level.Name;
			}

			// Renderizado SharpDX
			SharpDX.DirectWrite.TextFormat textFormat = LabelFont.ToDirectWriteTextFormat();
			SharpDX.Direct2D1.Brush textBrush = LabelColor.ToDxBrush(RenderTarget);

			foreach (var entry in groupedForRender)
			{
				float y = chartScale.GetYByValue(entry.Key);
				
				// Solo dibujar si está dentro del área visible vertical
				if (y >= 0 && y <= RenderTarget.Size.Height)
				{
					string text = entry.Value;
					
					// Usamos un ancho mayor para el layout y evitamos que se corte
					SharpDX.DirectWrite.TextLayout textLayout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, textFormat, 1000, textFormat.FontSize);
					
					// Posición ajustada por el Offset configurable
					float x = RenderTarget.Size.Width - textLayout.Metrics.Width - LabelRightOffset;
					
					RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y - (textLayout.Metrics.Height / 2)), textLayout, textBrush);
					
					textLayout.Dispose();
				}
			}
			
			textBrush.Dispose();
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name="URL de Datos CSV", Description="Enlace público al archivo CSV (Dropbox, Google Drive, etc.)", GroupName="Parameters", Order=1)]
		public string DataUrl { get; set; }

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

		[NinjaScriptProperty]
		[Display(Name="Right Offset", GroupName="Visuals", Order=6)]
		public int LabelRightOffset { get; set; }
		#endregion
	}
}


