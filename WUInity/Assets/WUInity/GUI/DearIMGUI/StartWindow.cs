using ImGuiNET;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public static class StartWindow
    {
        private static bool _isOpen;

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
        }

        public static void Close()
        {
            if (_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
            _isOpen = false;
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            ImGui.Begin("Welcome to PREACT", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            ImGui.Text("Load or create a new .wui file to run simulations.");
            if (ImGui.Button("OK")) { Close(); }

            ImGui.SeparatorText("Environment");
            ImGui.Text("This PREACT build no longer exposes PATH diagnostics in the UI.");

            ImGui.End();    
            if(!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }

        }
    }
}
