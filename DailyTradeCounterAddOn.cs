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
        private ComboBox accountSelector;
        private Account selectedAccount;
        private System.Windows.Threading.DispatcherTimer fallbackTimer;

        public DailyTradeCounterWindow()
        {
            Caption = "Trade Counter Pro";
            Width = 280;
            Height = 250;
            Topmost = true;

            Grid mainGrid = new Grid { Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Selector
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Count
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
            refreshBtn.Click += (s, e) => RefreshCount();

            centerPanel.Children.Add(countLabel);
            centerPanel.Children.Add(refreshBtn);
            Grid.SetRow(centerPanel, 1);
            mainGrid.Children.Add(centerPanel);

            // 3. FOOTER: Status
            statusLabel = new TextBlock { 
                Text = "Listo", 
                Foreground = Brushes.DarkGray, 
                FontSize = 10, 
                Margin = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Right 
            };
            Grid.SetRow(statusLabel, 2);
            mainGrid.Children.Add(statusLabel);

            Content = mainGrid;

            // Timer de seguridad (cada 5 seg)
            fallbackTimer = new System.Windows.Threading.DispatcherTimer();
            fallbackTimer.Interval = TimeSpan.FromSeconds(5);
            fallbackTimer.Tick += (s, e) => RefreshCount();
            fallbackTimer.Start();

            UpdateSelectedAccount();
            
            this.Closed += (s, e) => {
                if (selectedAccount != null) selectedAccount.ExecutionUpdate -= OnExecutionUpdate;
                if (fallbackTimer != null) fallbackTimer.Stop();
            };
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
                        foreach (object execObj in execsList)
                        {
                            if (execObj == null) continue;

                            var timeProp = execObj.GetType().GetProperty("Time");
                            var nameProp = execObj.GetType().GetProperty("Name");
                            
                            if (timeProp != null && nameProp != null)
                            {
                                DateTime execTime = (DateTime)timeProp.GetValue(execObj);
                                string name = nameProp.GetValue(execObj).ToString();
                                
                                if (execTime >= today)
                                {
                                    lastExecName = name; // Guardamos para debug
                                    string nLower = name.ToLower();

                                    // Filtro expandido: Buscamos cualquier indicio de cierre
                                    // En ATM suele ser "Profit target" o "Stop loss"
                                    if (nLower.Contains("exit") || nLower.Contains("target") || 
                                        nLower.Contains("stop") || nLower.Contains("close") || nLower.Contains("cerrar"))
                                    {
                                        count++;
                                    }
                                }
                            }
                        }
                    }
                }
                
                countLabel.Text = count.ToString();
                // Mostramos el nombre de la última ejecución para saber qué detecta NT8
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
