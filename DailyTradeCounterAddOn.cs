#region Using declarations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public class DailyTradeCounterAddOn : AddOnBase
    {
        private NTMenuItem addonMenuItem;
        private NTMenuItem toolsMenu;
        private bool menuAdded = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Contador de trades diarios con selector de cuenta.";
                Name = "Daily Trade Counter Pro";
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            if (!(window is ControlCenter cc) || menuAdded)
                return;

            toolsMenu = cc.FindFirst("ControlCenterMenuItemTools") as NTMenuItem;
            if (toolsMenu != null)
            {
                foreach (var dup in toolsMenu.Items.OfType<NTMenuItem>()
                         .Where(i => (string)i.Header == "Contador de Trades Diarios").ToArray())
                    toolsMenu.Items.Remove(dup);

                addonMenuItem = new NTMenuItem
                {
                    Header = "Contador de Trades Diarios",
                    Style  = Application.Current.TryFindResource("MainMenuItem") as Style
                };

                addonMenuItem.Click += (s, e) =>
                {
                    Core.Globals.RandomDispatcher.BeginInvoke(new Action(() => {
                        new DailyTradeCounterWindow().Show();
                    }));
                };

                toolsMenu.Items.Add(addonMenuItem);
                menuAdded = true;
            }
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (addonMenuItem != null && toolsMenu != null)
            {
                toolsMenu.Items.Remove(addonMenuItem);
                menuAdded = false;
            }
        }
    }

    public class DailyTradeCounterWindow : NTWindow
    {
        private TextBlock countLabel;
        private TextBlock statusLabel;
        private TextBlock phraseLabel;
        private ComboBox accountSelector;
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

        public DailyTradeCounterWindow()
        {
            Caption = "Trade Counter Pro";
            Width = 300;
            Height = 350;
            Topmost = true;

            Grid mainGrid = new Grid { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Selector
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Count
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Phrase
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer/Status

            // 1. TOP: Account Selector
            StackPanel topPanel = new StackPanel { Margin = new Thickness(10) };
            topPanel.Children.Add(new TextBlock { Text = "CUENTA:", Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(0,0,0,5) });
            
            accountSelector = new ComboBox { Height = 25 };
            foreach (Account acc in Account.All) accountSelector.Items.Add(acc.Name);

            string defaultAcc = Account.All.FirstOrDefault(a => a.Name != "PlaybackConnection")?.Name;
            if (defaultAcc != null) accountSelector.SelectedItem = defaultAcc;

            accountSelector.SelectionChanged += (s, e) => UpdateSelectedAccount();
            topPanel.Children.Add(accountSelector);
            Grid.SetRow(topPanel, 0);
            mainGrid.Children.Add(topPanel);

            // 2. CENTER: Big Number & Refresh Button
            StackPanel centerPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            countLabel = new TextBlock { 
                Text = "0", 
                Foreground = Brushes.Lime, 
                FontSize = 80, 
                FontWeight = FontWeights.Bold, 
                HorizontalAlignment = HorizontalAlignment.Center 
            };
            
            Button refreshBtn = new Button { 
                Content = "REFRESCAR", 
                Width = 80, 
                Height = 20, 
                FontSize = 9, 
                Margin = new Thickness(0,10,0,0),
                Background = Brushes.DimGray,
                Foreground = Brushes.White
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
                FontSize = 13,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(15, 10, 15, 20),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetRow(phraseLabel, 2);
            mainGrid.Children.Add(phraseLabel);

            // 4. FOOTER: Status
            statusLabel = new TextBlock { 
                Text = "Listo", 
                Foreground = Brushes.DarkGray, 
                FontSize = 10, 
                Margin = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Right 
            };
            Grid.SetRow(statusLabel, 3);
            mainGrid.Children.Add(statusLabel);

            Content = mainGrid;

            // Timer de seguridad para el conteo de trades (cada 5 seg)
            fallbackTimer = new System.Windows.Threading.DispatcherTimer();
            fallbackTimer.Interval = TimeSpan.FromSeconds(5);
            fallbackTimer.Tick += (s, e) => RefreshCount();
            fallbackTimer.Start();

            // Timer para las frases motivadoras (cada 5 minutos)
            phraseTimer = new System.Windows.Threading.DispatcherTimer();
            phraseTimer.Interval = TimeSpan.FromMinutes(5);
            phraseTimer.Tick += (s, e) => RotatePhrase();
            phraseTimer.Start();

            UpdateSelectedAccount();
            
            this.Closed += (s, e) => {
                if (selectedAccount != null) selectedAccount.ExecutionUpdate -= OnExecutionUpdate;
                if (fallbackTimer != null) fallbackTimer.Stop();
                if (phraseTimer != null) phraseTimer.Stop();
            };
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

        private void UpdateSelectedAccount()
        {
            string accName = accountSelector.SelectedItem as string;
            if (string.IsNullOrEmpty(accName)) return;

            if (selectedAccount != null)
                selectedAccount.ExecutionUpdate -= OnExecutionUpdate;

            selectedAccount = Account.All.FirstOrDefault(a => a.Name == accName);

            if (selectedAccount != null)
            {
                selectedAccount.ExecutionUpdate += OnExecutionUpdate;
                RefreshCount();
                statusLabel.Text = "Conectado a " + accName;
            }
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            // Pequeño retardo para dar tiempo a NT8 a cerrar el Trade
            Dispatcher.InvokeAsync(async () => {
                await System.Threading.Tasks.Task.Delay(1000); 
                RefreshCount();
            });
        }

        private void RefreshCount()
        {
            if (selectedAccount == null) return;

            try
            {
                DateTime today = DateTime.Today;
                int count = 0;
                string lastExecName = "N/A";

                // Acceso vía REFLEXIÓN a las Ejecuciones
                var execsProp = selectedAccount.GetType().GetProperty("Executions");
                if (execsProp != null)
                {
                    var execsList = execsProp.GetValue(selectedAccount) as System.Collections.IEnumerable;
                    if (execsList != null)
                    {
                        // Usamos un HashSet para recordar los OrderId que ya hemos contado hoy
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
                                        {
                                            orderId = orderIdProp.GetValue(orderObj)?.ToString() ?? "";
                                        }
                                    }

                                    string nLower = name.ToLower();

                                    // Filtro para contabilizar solo las ejecuciones que cierran un trade
                                    if (nLower.Contains("exit") || nLower.Contains("target") || 
                                        nLower.Contains("stop") || nLower.Contains("close") || 
                                        nLower.Contains("cerrar") || nLower.Contains("rev"))
                                    {
                                        lastExecName = name; // Guardamos para debug

                                        // Si la orden tiene un ID y no lo hemos contado antes, sumamos 1
                                        if (!string.IsNullOrEmpty(orderId))
                                        {
                                            if (!countedOrders.Contains(orderId))
                                            {
                                                countedOrders.Add(orderId);
                                                count++;
                                            }
                                        }
                                        else
                                        {
                                            // Fallback por si no se pudiera obtener el OrderId (muy raro)
                                            count++;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                
                countLabel.Text = count.ToString();
                statusLabel.Text = string.Format("Last: {0} | {1}", 
                    lastExecName.Length > 10 ? lastExecName.Substring(0, 10) : lastExecName, 
                    DateTime.Now.ToString("HH:mm:ss"));
            }
            catch (Exception)
            {
                statusLabel.Text = "Err: Ver Log";
            }
        }
    }
}
