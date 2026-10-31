using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using ButterflyGammaPro.Models;

namespace ButterflyGammaPro.Services
{
    public static class ConfigManager
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null && config.Profiles.Count > 0)
                    {
                        return config;
                    }
                }
            }
            catch { }

            var defaultConfig = CreateDefaultConfig();
            Save(defaultConfig);
            return defaultConfig;
        }

        public static void Save(AppConfig config)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
            }
        }

        public static AppConfig CreateDefaultConfig()
        {
            var config = new AppConfig
            {
                EnableOsd = true,
                EnableTransition = true,
                AntiResetLock = true,
                CloseBehavior = 0,
                RememberCloseChoice = false
            };

            var pDefault = new GammaProfile
            {
                Id = "default_outdoor",
                Name = "【日常/室外】恢复正常屏幕",
                KeyName = "F7",
                VirtualKey = 0x76,
                IsReadOnly = true,
                Red = new ColorChannel { Gamma = 100, Brightness = 0, Contrast = 100 },
                Green = new ColorChannel { Gamma = 100, Brightness = 0, Contrast = 100 },
                Blue = new ColorChannel { Gamma = 100, Brightness = 0, Contrast = 100 }
            };

            var pIndoor = new GammaProfile
            {
                Id = "tarkov_indoor",
                Name = "【游戏内】室内/暗部 强力提亮",
                KeyName = "F8",
                VirtualKey = 0x77,
                Red = new ColorChannel { Gamma = 220, Brightness = -5, Contrast = 135 },
                Green = new ColorChannel { Gamma = 220, Brightness = -5, Contrast = 135 },
                Blue = new ColorChannel { Gamma = 220, Brightness = -5, Contrast = 135 }
            };

            var pBunker = new GammaProfile
            {
                Id = "tarkov_bunker",
                Name = "【游戏内】极夜/地道 极限透光",
                KeyName = "F9",
                VirtualKey = 0x78,
                Red = new ColorChannel { Gamma = 250, Brightness = -8, Contrast = 145 },
                Green = new ColorChannel { Gamma = 250, Brightness = -8, Contrast = 145 },
                Blue = new ColorChannel { Gamma = 250, Brightness = -8, Contrast = 145 }
            };

            config.Profiles.Add(pDefault);
            config.Profiles.Add(pIndoor);
            config.Profiles.Add(pBunker);
            config.ActiveProfileId = pDefault.Id;

            return config;
        }
    }
}
