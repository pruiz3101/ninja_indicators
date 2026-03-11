#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
#endregion

public enum PanelPosition
{
    BottomRight,
    BottomLeft
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class TradeCounterPanel : Indicator
    {
        private Grid mainGrid;
        private TextBlock countLabel;
        private TextBlock statusLabel;
        private TextBlock phraseLabel;
        private ComboBox accountSelector;
        private Button refreshBtn;
        
        private Account selectedAccount;
        private System.Windows.Threading.DispatcherTimer fallbackTimer;
        private System.Windows.Threading.DispatcherTimer phraseTimer;
        private int currentPhraseIndex = 0;

        private readonly List<string> phrases = new List<string>
        {
            "Respeta tu plan de trading a rajatabla.",
            "La paciencia paga. Espera tu setup perfecto.",
            "Gestiona tu riesgo, el mercado es impredecible.",
            "No persigas el precio, deja que venga a ti.",
            "Un stop loss es tu salvavidas, no tu enemigo.",
            "El trading es un maratón, no un sprint.",
            "Protege tu capital primero, busca ganancias después.",
            "Acepta la pérdida rápidamente y sigue adelante.",
            "Opera lo que ves, no lo que crees.",
            "Menos es más. Selecciona solo las mejores oportunidades.",
            "La disciplina es el puente entre tus metas y el éxito.",
            "Mantén la calma y confía en tu estrategia.",
            "Tu única competencia en el mercado eres tú mismo.",
            "Deja correr tus ganancias y corta tus pérdidas.",
            "El mercado estará aquí mañana, no fuerces un trade.",
            "Celebra tus buenas decisiones, no solo los resultados.",
            "Conoce cuándo no operar, quedarse fuera es una posición.",
            "Un buen trade perdedor es aquel que siguió el plan.",
            "Mantén tus emociones fuera de la pantalla.",
            "Consistencia antes que rentabilidad."
        };

        private bool isControlAdded = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                                 = @"Panel incrustado para contar trades diarios en las esquinas inferiores.";
                Name                                        = "TradeCounterPanel";
                Calculate                                   = Calculate.OnBarClose;
                IsOverlay                                   = true;
                DisplayInDataBox                            = false;
                DrawOnPricePanel                            = true;
                DrawHorizontalGridLines                     = false;
                DrawVerticalGridLines                       = false;
                PaintPriceMarkers                           = false;
                ScaleJustification                          = NinjaTrader.Gui.Chart.ScaleJustification.Right;
                IsSuspendedWhileInactive                    = true;
                
                // Configuración general del panel
                PanelColor = Brushes.Black;
                PanelOpacity = 0.85;
                PanelAlignment = PanelPosition.BottomRight;
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null)
                {
                    Dispatcher.InvokeAsync((Action)(() => InsertWPFControls()));
                }
            }
            else if (State == State.Terminated)
            {
                if (ChartControl != null)
                {
                    Dispatcher.InvokeAsync((Action)(() => RemoveWPFControls()));
                }
                
                if (selectedAccount != null)
                    selectedAccount.ExecutionUpdate -= OnExecutionUpdate;
                
                if (fallbackTimer != null) fallbackTimer.Stop();
                if (phraseTimer != null) phraseTimer.Stop();
            }
        }

        private void InsertWPFControls()
        {
            if (isControlAdded) return;

            // Envolvemos el Grid en un Border con un color sólido oscuro y opacidad alta.
            // Ahora se ajusta limpiamente a la esquina seleccionada dentro del gráfico.
            Border panelBorder = new Border
            {
                Background = PanelColor.Clone(),
                Width = 220, // Un poco más esbelto para no estorbar mucho el gráfico
                Margin = new Thickness(15, 15, 15, 25), // Márgenes limpios para separarlo un pelín del borde
                HorizontalAlignment = (PanelAlignment == PanelPosition.BottomRight) ? HorizontalAlignment.Right : HorizontalAlignment.Left,  
                VerticalAlignment = VerticalAlignment.Bottom,
                BorderThickness = new Thickness(0) // Sin bordes
            };
            panelBorder.Background.Opacity = PanelOpacity;

            mainGrid = new Grid { Margin = new Thickness(10) };
            
            // Filas: 0=Titulo, 1=Contador, 2=Frase, 3=Status
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 1. TOP: Title (no selector anymore)
            StackPanel topPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            TextBlock titleText = new TextBlock { Text = "TRADE COUNTER", Foreground = Brushes.Orange, FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            
            // accountSelector = new ComboBox { 
            //     Height = 22, 
            //     FontSize = 11,
            //     Background = Brushes.DarkGray, 
            //     Foreground = Brushes.Black 
            // };
            
            // foreach (Account acc in Account.All) 
            //     accountSelector.Items.Add(acc.Name);

            // string defaultAcc = Account.All.FirstOrDefault(a => a.Name != "PlaybackConnection")?.Name;
            // if (defaultAcc != null) accountSelector.SelectedItem = defaultAcc;
            // accountSelector.SelectionChanged += (s, e) => UpdateSelectedAccount(); // Removed
            
            topPanel.Children.Add(titleText);
            // topPanel.Children.Add(accountSelector); // Removed
            Grid.SetRow(topPanel, 0);
            mainGrid.Children.Add(topPanel);

            // 2. CENTER: Big Number & Refresh Button
            StackPanel centerPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            countLabel = new TextBlock { 
                Text = "0", 
                Foreground = Brushes.Lime, 
                FontSize = 60,
                FontWeight = FontWeights.Bold, 
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0,0,0,10)
            };
            
            refreshBtn = new Button { 
                Content = "RELOAD", 
                Width = 60, 
                Height = 18, 
                FontSize = 9, 
                Background = Brushes.DimGray,
                Foreground = Brushes.White,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            refreshBtn.Click += (s, e) => {
                RefreshCount();
                RotatePhrase();
            };

            centerPanel.Children.Add(countLabel);
            centerPanel.Children.Add(refreshBtn);
            Grid.SetRow(centerPanel, 1);
            mainGrid.Children.Add(centerPanel);

            // 3. PHRASE PANEL
            Random rConfig = new Random();
            currentPhraseIndex = rConfig.Next(phrases.Count);
            phraseLabel = new TextBlock {
                Text = "\"" + phrases[currentPhraseIndex] + "\"",
                Foreground = Brushes.Gold,
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(5, 15, 5, 15),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetRow(phraseLabel, 2);
            mainGrid.Children.Add(phraseLabel);

            // 4. FOOTER: Status
            statusLabel = new TextBlock { 
                Text = "Buscando Cuenta...", 
                Foreground = Brushes.Gray, 
                FontSize = 9, 
                HorizontalAlignment = HorizontalAlignment.Right 
            };
            Grid.SetRow(statusLabel, 3);
            mainGrid.Children.Add(statusLabel);

            panelBorder.Child = mainGrid;

            // --- Inyectar en el gráfico ---
            if (UserControlCollection != null)
                UserControlCollection.Add(panelBorder);
            
            isControlAdded = true;

            // Timers
            fallbackTimer = new System.Windows.Threading.DispatcherTimer();
            fallbackTimer.Interval = TimeSpan.FromSeconds(2); // Revisión más rápida para agarrar la cuenta
            fallbackTimer.Tick += (s, e) => SyncChartTraderAccount();
            fallbackTimer.Start();

            phraseTimer = new System.Windows.Threading.DispatcherTimer();
            phraseTimer.Interval = TimeSpan.FromMinutes(5);
            phraseTimer.Tick += (s, e) => RotatePhrase();
            phraseTimer.Start();

            SyncChartTraderAccount(); // Initial call to sync account
        }

        private void RemoveWPFControls()
        {
            if (isControlAdded && mainGrid != null)
            {
                var parent = mainGrid.Parent as Border;
                if(parent != null && UserControlCollection != null)
                {
                    UserControlCollection.Remove(parent);
                }
                isControlAdded = false;
                mainGrid = null;
            }
        }

        private void RotatePhrase()
        {
            Random r = new Random();
            int newIndex;
            do {
                newIndex = r.Next(phrases.Count);
            } while (newIndex == currentPhraseIndex && phrases.Count > 1);
            currentPhraseIndex = newIndex;
            
            if (phraseLabel != null)
                phraseLabel.Text = "\"" + phrases[currentPhraseIndex] + "\"";
        }

        // private void UpdateSelectedAccount() // Removed
        // {
        //     string accName = accountSelector.SelectedItem as string;
        //     if (string.IsNullOrEmpty(accName)) return;

        //     if (selectedAccount != null)
        //         selectedAccount.ExecutionUpdate -= OnExecutionUpdate;

        //     selectedAccount = Account.All.FirstOrDefault(a => a.Name == accName);

        //     if (selectedAccount != null)
        //     {
        //         selectedAccount.ExecutionUpdate += OnExecutionUpdate;
        //         RefreshCount();
        //         statusLabel.Text = accName;
        //     }
        // }

        private void SyncChartTraderAccount()
        {
            if (ChartControl == null || ChartControl.OwnerChart == null) return;
            
            // Accedemos directamente a la ventana padre (Chart) y miramos su ChartTraderActivo
            // El objeto OwnerChart hereda de Chart que tiene una propiedad ChartTrader pública.
            if (ChartControl.OwnerChart.ChartTrader != null)
            {
                Account ctAccount = ChartControl.OwnerChart.ChartTrader.Account;
                
                if (ctAccount != null)
                {
                    // Evitamos reconectarnos si es la misma cuenta
                    if (selectedAccount == null || selectedAccount.Name != ctAccount.Name)
                    {
                        if (selectedAccount != null)
                            selectedAccount.ExecutionUpdate -= OnExecutionUpdate;
                        
                        selectedAccount = ctAccount;
                        selectedAccount.ExecutionUpdate += OnExecutionUpdate;
                        
                        // Forzamos actualización visual en el hilo de la UI
                        Dispatcher.InvokeAsync(() => {
                            if (statusLabel != null) statusLabel.Text = selectedAccount.Name;
                        });
                        RefreshCount();
                    }
                    return; 
                }
            }
            
            // Fallback si no hay ChartTrader habilitado
            if (selectedAccount == null)
            {
                string fallbackAccName = Account.All.FirstOrDefault(a => a.Name != "PlaybackConnection")?.Name;
                if (!string.IsNullOrEmpty(fallbackAccName))
                {
                    selectedAccount = Account.All.FirstOrDefault(a => a.Name == fallbackAccName);
                    if (selectedAccount != null)
                    {
                        selectedAccount.ExecutionUpdate += OnExecutionUpdate;
                        RefreshCount();
                        Dispatcher.InvokeAsync(() => {
                            if (statusLabel != null) statusLabel.Text = "CT Inactivo: Usa " + selectedAccount.Name;
                        });
                    }
                }
            }
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (Dispatcher == null) return;
            
            // InvokeAsync asegura que actualicemos la UI desde el hilo correcto del gráfico
            Dispatcher.InvokeAsync(async () => {
                await System.Threading.Tasks.Task.Delay(1000); 
                RefreshCount();
            });
        }

        private void RefreshCount()
        {
            if (selectedAccount == null || countLabel == null || Dispatcher == null) return;

            try
            {
                DateTime today = DateTime.Today;
                int count = 0;
                string lastExecName = "N/A";

                var execsProp = selectedAccount.GetType().GetProperty("Executions");
                if (execsProp != null)
                {
                    var execsList = execsProp.GetValue(selectedAccount) as System.Collections.IEnumerable;
                    if (execsList != null)
                    {
                        HashSet<string> countedOrders = new HashSet<string>();

                        foreach (object execObj in execsList)
                        {
                            if (execObj == null) continue;

                            var timeProp = execObj.GetType().GetProperty("Time");
                            var nameProp = execObj.GetType().GetProperty("Name");
                            var orderProp = execObj.GetType().GetProperty("Order");
                            
                            if (timeProp != null && nameProp != null && orderProp != null)
                            {
                                DateTime execTime = (DateTime)timeProp.GetValue(execObj);
                                string name = nameProp.GetValue(execObj).ToString();
                                
                                if (execTime >= today)
                                {
                                    object orderObj = orderProp.GetValue(execObj);
                                    string orderId = "";
                                    if (orderObj != null)
                                    {
                                        var orderIdProp = orderObj.GetType().GetProperty("OrderId");
                                        if (orderIdProp != null)
                                            orderId = orderIdProp.GetValue(orderObj)?.ToString() ?? "";
                                    }

                                    string nLower = name.ToLower();

                                    if (nLower.Contains("exit") || nLower.Contains("target") || 
                                        nLower.Contains("stop") || nLower.Contains("close") || 
                                        nLower.Contains("cerrar") || nLower.Contains("rev"))
                                    {
                                        lastExecName = name; 

                                        if (!string.IsNullOrEmpty(orderId))
                                        {
                                            if (!countedOrders.Contains(orderId))
                                            {
                                                countedOrders.Add(orderId);
                                                count++;
                                            }
                                        }
                                        else count++;
                                    }
                                }
                            }
                        }
                    }
                }
                
                Dispatcher.InvokeAsync(() => {
                    if (countLabel != null) countLabel.Text = count.ToString();
                    if (statusLabel != null) statusLabel.Text = string.Format("Last: {0} | {1}", 
                        lastExecName.Length > 8 ? lastExecName.Substring(0, 8) : lastExecName, 
                        DateTime.Now.ToString("HH:mm"));
                });
            }
            catch (Exception)
            {
                Dispatcher.InvokeAsync(() => {
                    if (statusLabel != null) statusLabel.Text = "Eval Err";
                });
            }
        } // <- FALTABA ESTA LLAVE DE CIERRE PARA EL MÉTODO

        #region Properties
        [NinjaScriptProperty]
        [Display(Name="Opacidad del Panel", Description="Transparencia del panel (0.0 a 1.0)", Order=2, GroupName="Parameters")]
        [Range(0.0, 1.0)]
        public double PanelOpacity { get; set; }

        [XmlIgnore]
        [Display(Name="Color de Fondo", Description="Color del fondo del panel", Order=3, GroupName="Parameters")]
        public Brush PanelColor { get; set; }

        [Browsable(false)]
        public string PanelColorSerialize
        {
            get { return Serialize.BrushToString(PanelColor); }
            set { PanelColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name="Posición del Panel", Description="Esquina en la que se ancla el panel (Derecha Abajo o Izquierda Abajo)", Order=4, GroupName="Parameters")]
        public PanelPosition PanelAlignment { get; set; }
        #endregion
    }
}
