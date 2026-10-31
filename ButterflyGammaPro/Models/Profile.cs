using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ButterflyGammaPro.Models
{
    public class ColorChannel
    {
        public int Gamma { get; set; } = 100;
        public int Brightness { get; set; } = 0;
        public int Contrast { get; set; } = 100;

        public ColorChannel Clone()
        {
            return new ColorChannel
            {
                Gamma = this.Gamma,
                Brightness = this.Brightness,
                Contrast = this.Contrast
            };
        }
    }

    public class GammaProfile : INotifyPropertyChanged
    {
        private string _name = "新滤镜";
        private string _keyName = "无";
        private bool _isActive = false;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string KeyName
        {
            get => _keyName;
            set { _keyName = value; OnPropertyChanged(); }
        }

        public uint VirtualKey { get; set; } = 0;
        public uint Modifiers { get; set; } = 0;
        public bool IsReadOnly { get; set; } = false;

        [JsonIgnore]
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public bool CanDelete => !IsReadOnly;

        public ColorChannel Red { get; set; } = new();
        public ColorChannel Green { get; set; } = new();
        public ColorChannel Blue { get; set; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        public void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public GammaProfile Clone(string newName)
        {
            return new GammaProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = newName,
                KeyName = "无",
                VirtualKey = 0,
                Modifiers = 0,
                IsReadOnly = false,
                Red = this.Red.Clone(),
                Green = this.Green.Clone(),
                Blue = this.Blue.Clone()
            };
        }
    }

    public class AppConfig
    {
        public bool EnableOsd { get; set; } = true;
        public bool EnableTransition { get; set; } = true;
        public bool AntiResetLock { get; set; } = true;
        public string ActiveProfileId { get; set; } = "";
        
        public int CloseBehavior { get; set; } = 0;
        public bool RememberCloseChoice { get; set; } = false;

        public List<GammaProfile> Profiles { get; set; } = new();
    }
}
