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
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators
{
	public class AMDProIndicator : Indicator
	{
		private double asianHigh = double.MinValue;
		private double asianLow = double.MaxValue;
		private bool isAsianSession = false;
		
		private double londonHigh = double.MinValue;
		private double londonLow = double.MaxValue;
		private bool isLondonSession = false;

		private bool bullishSweepDetected = false;
		private bool bearishSweepDetected = false;
		private bool bullishMSSDetected = false;
		private bool bearishMSSDetected = false;
		private int lastSweepBar = -1;
		
		private Gui.Tools.SimpleFont labelFont = new Gui.Tools.SimpleFont("Arial", 10);
		private Gui.Tools.SimpleFont hudFont = new Gui.Tools.SimpleFont("Consolas", 12);

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"AMD Pro Indicator for Scalping - Accumulation, Manipulation, Distribution";
				Name										= "AMDProIndicator";
				Calculate									= Calculate.OnPriceChange;
				IsOverlay									= true;
				DisplayInDataBox							= true;
				DrawOnPricePanel							= true;
				DrawHorizontalGridLines						= true;
				DrawVerticalGridLines						= true;
				PaintPriceMarkers							= true;
				ScaleJustification							= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				//Disable this property if your indicator requires custom values that cumulate with each new market data event. 
				//See Help Guide for additional information.
				IsSuspendedWhileInactive					= true;
				
				AsianStartTime = DateTime.Parse("20:00", System.Globalization.CultureInfo.InvariantCulture);
				AsianEndTime = DateTime.Parse("02:00", System.Globalization.CultureInfo.InvariantCulture);
				
				LondonStartTime = DateTime.Parse("03:00", System.Globalization.CultureInfo.InvariantCulture);
				LondonEndTime = DateTime.Parse("09:00", System.Globalization.CultureInfo.InvariantCulture);

				SweepThresholdTicks = 4;
				
				SweepThresholdTicks = 4;
				ShowFVG = true;
				ShowMSS = true;
				
				AsianBoxBrush = Brushes.DeepSkyBlue;
				AsianBoxOpacity = 10;
				
				LondonBoxBrush = Brushes.Orange;
				LondonBoxOpacity = 10;
				
				MSSBullishBrush = Brushes.Lime;
				MSSBearishBrush = Brushes.Red;

				BoxDashStyle = DashStyleHelper.Dash;
				ShowHUD = true;
				SignalOffsetTicks = 15;
				IconOffsetTicks = 8;
			}
		}

		private double lastSwingHigh = 0;
		private double lastSwingLow = 0;

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 20) return;

			DateTime time = Time[0];
			CheckSessions(time);
			
			if (isAsianSession)
			{
				if (High[0] > asianHigh) asianHigh = High[0];
				if (Low[0] < asianLow) asianLow = Low[0];
				DrawAsianRange();
			}
			
			if (isLondonSession)
			{
				if (High[0] > londonHigh) londonHigh = High[0];
				if (Low[0] < londonLow) londonLow = Low[0];
				DrawLondonRange();
			}

			// Don't detect sweeps while we are still accumulating Asia
			if (!isAsianSession)
			{
				DetectSweeps();
				if (ShowMSS) DetectMSS();
				if (ShowFVG) DetectFVG();
			}
			
			// Update swing points for MSS
			UpdateSwingPoints();

			if (ShowHUD) DrawHUD();
		}

		private void DrawHUD()
		{
			string status = "STATUS: ";
			if (isAsianSession) status += "ACCUMULATION (ASIA)";
			else if (isLondonSession) status += "ACCUMULATION (LDN)";
			else status += "DISTRIBUTION / MANIPULATION";

			Draw.TextFixed(this, "HUD", "AMD PRO | " + status, TextPosition.TopRight, Brushes.White, HUDFont, Brushes.DimGray, Brushes.Black, 80);
		}

		private int asianStartBar = -1;
		private int londonStartBar = -1;

		private void CheckSessions(DateTime time)
		{
			// Convert chart time to New York Time
			DateTime nyTime = time;
			try {
				nyTime = TimeZoneInfo.ConvertTime(time, TimeZoneInfo.Local, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
			} catch { /* Fallback to chart time if TZ not found */ }

			TimeSpan now = nyTime.TimeOfDay;
			
			// Asian Session
			TimeSpan aStart = AsianStartTime.TimeOfDay;
			TimeSpan aEnd = AsianEndTime.TimeOfDay;
			bool inAsian = (aStart < aEnd) ? (now >= aStart && now < aEnd) : (now >= aStart || now < aEnd);

			if (inAsian)
			{
				if (!isAsianSession)
				{
					asianHigh = High[0];
					asianLow = Low[0];
					isAsianSession = true;
					ResetTriggers();
					asianStartBar = CurrentBar;
					Print("Asian Session Started at Bar " + CurrentBar);
				}
			}
			else
			{
				isAsianSession = false;
			}

			// London Session
			TimeSpan lStart = LondonStartTime.TimeOfDay;
			TimeSpan lEnd = LondonEndTime.TimeOfDay;
			bool inLondon = (lStart < lEnd) ? (now >= lStart && now < lEnd) : (now >= lStart || now < lEnd);

			if (inLondon)
			{
				if (!isLondonSession)
				{
					londonHigh = High[0];
					londonLow = Low[0];
					isLondonSession = true;
					ResetTriggers();
					londonStartBar = CurrentBar;
					Print("London Session Started at Bar " + CurrentBar);
				}
			}
			else
			{
				isLondonSession = false;
			}
		}

		private void ResetTriggers()
		{
			bullishSweepDetected = false;
			bearishSweepDetected = false;
			bullishMSSDetected = false;
			bearishMSSDetected = false;
			asianSweepDetected = false;
			londonSweepDetected = false;
			lastSweepBar = -1;
		}

		private void DrawAsianRange()
		{
			if (asianStartBar == -1) return;
			int barsAgo = CurrentBar - asianStartBar;
			Draw.Rectangle(this, "AsianRange" + asianStartBar, true, barsAgo, asianHigh, 0, asianLow, Brushes.Transparent, AsianBoxBrush, AsianBoxOpacity);
			Draw.Text(this, "AsianLabel" + asianStartBar, true, "ASIA", barsAgo, asianHigh, 0, AsianBoxBrush, LabelFont, TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
		}

		private void DrawLondonRange()
		{
			if (londonStartBar == -1) return;
			int barsAgo = CurrentBar - londonStartBar;
			Draw.Rectangle(this, "LondonRange" + londonStartBar, true, barsAgo, londonHigh, 0, londonLow, Brushes.Transparent, LondonBoxBrush, LondonBoxOpacity);
			Draw.Text(this, "LondonLabel" + londonStartBar, true, "LONDON", barsAgo, londonHigh, 0, LondonBoxBrush, LabelFont, TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
		}

		private bool asianSweepDetected = false;
		private bool londonSweepDetected = false;

		private void DetectSweeps()
		{
			// Check Asian Sweep
			if (!asianSweepDetected && asianHigh != double.MinValue)
			{
				if (High[0] > (asianHigh + SweepThresholdTicks * TickSize))
				{
					TriggerSweep("ASIA H", Brushes.Red, true);
					asianSweepDetected = true;
				}
				else if (Low[0] < (asianLow - SweepThresholdTicks * TickSize))
				{
					TriggerSweep("ASIA L", Brushes.Green, false);
					asianSweepDetected = true;
				}
			}

			// Check London Sweep (only if not currently accumulating London)
			if (!londonSweepDetected && !isLondonSession && londonHigh != double.MinValue)
			{
				if (High[0] > (londonHigh + SweepThresholdTicks * TickSize))
				{
					TriggerSweep("LDN H", Brushes.OrangeRed, true);
					londonSweepDetected = true;
				}
				else if (Low[0] < (londonLow - SweepThresholdTicks * TickSize))
				{
					TriggerSweep("LDN L", Brushes.DarkGreen, false);
					londonSweepDetected = true;
				}
			}

			// Draw Session Liquidity Lines (Extended H/L until sweep)
			if (!asianSweepDetected && asianHigh != double.MinValue)
			{
				Draw.Line(this, "AsianHighLine", false, asianStartBar == -1 ? 0 : CurrentBar - asianStartBar, asianHigh, 0, asianHigh, AsianBoxBrush, DashStyleHelper.Dash, 1);
				Draw.Line(this, "AsianLowLine", false, asianStartBar == -1 ? 0 : CurrentBar - asianStartBar, asianLow, 0, asianLow, AsianBoxBrush, DashStyleHelper.Dash, 1);
			}
			if (!londonSweepDetected && londonHigh != double.MinValue)
			{
				Draw.Line(this, "LondonHighLine", false, londonStartBar == -1 ? 0 : CurrentBar - londonStartBar, londonHigh, 0, londonHigh, LondonBoxBrush, DashStyleHelper.Dash, 1);
				Draw.Line(this, "LondonLowLine", false, londonStartBar == -1 ? 0 : CurrentBar - londonStartBar, londonLow, 0, londonLow, LondonBoxBrush, DashStyleHelper.Dash, 1);
			}
		}

		private void TriggerSweep(string label, Brush color, bool isHigh)
		{
			bullishSweepDetected = !isHigh;
			bearishSweepDetected = isHigh;
			lastSweepBar = CurrentBar;
			
			double yPos = isHigh ? High[0] + SignalOffsetTicks * TickSize : Low[0] - SignalOffsetTicks * TickSize;
			
			// White Dot for Institutional Action (Vertical offset adjusted for label font size)
			Draw.Text(this, "SweepDot" + label + CurrentBar, true, "●", 0, yPos, -20, Brushes.White, LabelFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			// Colored Session Label
			Draw.Text(this, "Sweep" + label + CurrentBar, true, label, 0, yPos, 15, color, LabelFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			
			if (isHigh) Draw.TriangleDown(this, "SweepIcon" + label + CurrentBar, true, 0, High[0] + IconOffsetTicks * TickSize, color);
			else Draw.TriangleUp(this, "SweepIcon" + label + CurrentBar, true, 0, Low[0] - IconOffsetTicks * TickSize, color);

			// Remove extension lines on sweep
			if (label.Contains("ASIA")) { RemoveDrawObject("AsianHighLine"); RemoveDrawObject("AsianLowLine"); }
			if (label.Contains("LDN")) { RemoveDrawObject("LondonHighLine"); RemoveDrawObject("LondonLowLine"); }
		}

		private void DetectMSS()
		{
			if (lastSweepBar == -1 || lastSweepBar > CurrentBar) return;

			// Bullish MSS: After a bearish sweep, price breaks last swing high
			if (bearishSweepDetected && !bullishMSSDetected && High[0] > lastSwingHigh && lastSwingHigh > 0)
			{
				bullishMSSDetected = true;
				Draw.Line(this, "MSS" + CurrentBar, true, 0, lastSwingHigh, -10, lastSwingHigh, MSSBullishBrush, DashStyleHelper.Solid, 2);
				Draw.Text(this, "MSSText" + CurrentBar, true, "MSS BULL (+)", 0, lastSwingHigh + SignalOffsetTicks * TickSize, 0, MSSBullishBrush, LabelFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			}
			// Bearish MSS: After a bullish sweep, price breaks last swing low
			else if (bullishSweepDetected && !bearishMSSDetected && Low[0] < lastSwingLow && lastSwingLow > 0)
			{
				bearishMSSDetected = true;
				Draw.Line(this, "MSS" + CurrentBar, true, 0, lastSwingLow, -12, lastSwingLow, MSSBearishBrush, DashStyleHelper.Solid, 2);
				Draw.Text(this, "MSSText" + CurrentBar, true, "MSS BEAR ➔", 0, lastSwingLow - SignalOffsetTicks * TickSize, 0, MSSBearishBrush, LabelFont, TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
			}
		}

		private void UpdateSwingPoints()
		{
			if (CurrentBar < 2) return;
			// Simple 3-bar fractal for swing points
			if (High[1] > High[0] && High[1] > High[2]) lastSwingHigh = High[1];
			if (Low[1] < Low[0] && Low[1] < Low[2]) lastSwingLow = Low[1];
		}

		private void DetectFVG()
		{
			if (CurrentBar < 2) return;
			
			// Only show FVGs outside of active accumulation to avoid noise
			if (isAsianSession || isLondonSession) return;

			// Bullish FVG
			if (Low[0] > High[2])
			{
				Draw.Rectangle(this, "FVG_Bull" + CurrentBar, false, 2, Low[0], 0, High[2], Brushes.DimGray, Brushes.Lime, 5);
			}
			// Bearish FVG
			else if (High[0] < Low[2])
			{
				Draw.Rectangle(this, "FVG_Bear" + CurrentBar, false, 2, Low[2], 0, High[0], Brushes.DimGray, Brushes.Red, 5);
			}
		}

		private class FVG
		{
			public double High;
			public double Low;
			public int StartBar;
		}

		#region Properties
		[NinjaScriptProperty]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name="Asian Start", Order=1, GroupName="Sessions")]
		public DateTime AsianStartTime { get; set; }

		[NinjaScriptProperty]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name="Asian End", Order=2, GroupName="Sessions")]
		public DateTime AsianEndTime { get; set; }

		[NinjaScriptProperty]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name="London Start", Order=3, GroupName="Sessions")]
		public DateTime LondonStartTime { get; set; }

		[NinjaScriptProperty]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name="London End", Order=4, GroupName="Sessions")]
		public DateTime LondonEndTime { get; set; }

		[Range(1, 100)]
		[NinjaScriptProperty]
		[Display(Name="Sweep Threshold (Ticks)", Order=1, GroupName="Logic")]
		public int SweepThresholdTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Show FVG", Order=2, GroupName="Logic")]
		public bool ShowFVG { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Show MSS", Order=3, GroupName="Logic")]
		public bool ShowMSS { get; set; }

		[XmlIgnore]
		[Display(Name="Asian Box Color", Order=1, GroupName="Visuals")]
		public Brush AsianBoxBrush { get; set; }

		[Browsable(false)]
		public string AsianBoxBrushSerializable
		{
			get { return Serialize.BrushToString(AsianBoxBrush); }
			set { AsianBoxBrush = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[NinjaScriptProperty]
		[Display(Name="Asian Box Opacity", Order=2, GroupName="Visuals")]
		public int AsianBoxOpacity { get; set; }

		[XmlIgnore]
		[Display(Name="London Box Color", Order=3, GroupName="Visuals")]
		public Brush LondonBoxBrush { get; set; }

		[Browsable(false)]
		public string LondonBoxBrushSerializable
		{
			get { return Serialize.BrushToString(LondonBoxBrush); }
			set { LondonBoxBrush = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[NinjaScriptProperty]
		[Display(Name="London Box Opacity", Order=4, GroupName="Visuals")]
		public int LondonBoxOpacity { get; set; }

		[XmlIgnore]
		[Display(Name="MSS Bullish Color", Order=5, GroupName="Visuals")]
		public Brush MSSBullishBrush { get; set; }

		[Browsable(false)]
		public string MSSBullishBrushSerializable
		{
			get { return Serialize.BrushToString(MSSBullishBrush); }
			set { MSSBullishBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name="MSS Bearish Color", Order=6, GroupName="Visuals")]
		public Brush MSSBearishBrush { get; set; }

		[Browsable(false)]
		public string MSSBearishBrushSerializable
		{
			get { return Serialize.BrushToString(MSSBearishBrush); }
			set { MSSBearishBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name="Label Font", Order=7, GroupName="Visuals")]
		public Gui.Tools.SimpleFont LabelFont
		{
			get { return labelFont; }
			set { labelFont = value; }
		}

		[XmlIgnore]
		[Display(Name="HUD Font", Order=8, GroupName="Visuals")]
		public Gui.Tools.SimpleFont HUDFont
		{
			get { return hudFont; }
			set { hudFont = value; }
		}

		[NinjaScriptProperty]
		[Display(Name="Box Dash Style", Order=9, GroupName="Visuals")]
		public DashStyleHelper BoxDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Show HUD", Order=10, GroupName="Visuals")]
		public bool ShowHUD { get; set; }

		[Range(1, 100)]
		[NinjaScriptProperty]
		[Display(Name="Signal Offset (Ticks)", Order=11, GroupName="Visuals")]
		public int SignalOffsetTicks { get; set; }

		[Range(1, 100)]
		[NinjaScriptProperty]
		[Display(Name="Icon Offset (Ticks)", Order=12, GroupName="Visuals")]
		public int IconOffsetTicks { get; set; }
		#endregion
	}
}
