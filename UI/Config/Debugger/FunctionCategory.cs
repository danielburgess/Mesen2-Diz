using Avalonia.Media;
using System.Collections.Generic;

namespace Mesen.Config
{
	public enum FunctionCategory
	{
		None = 0,

		// Execution Backbone
		Init,
		MainLoop,
		Interrupt,
		DMA,

		// Input & Player
		Input,
		Player,

		// Graphics
		OAM,
		VRAM,
		Tilemap,
		Palette,
		Scrolling,
		Animation,
		Effects,
		Mode7,

		// Audio
		Music,
		SFX,

		// Gameplay Logic
		Physics,
		Collision,
		Entity,
		Enemy,
		AI,
		Camera,

		// Game Structure
		StateMachine,
		GameState,
		Menu,
		HUD,
		LevelLoad,
		Transition,

		// Script & Dialogue
		Script,
		Dialogue,

		// Data & Utility
		Math,
		RNG,
		Timer,
		Memory,
		Text,
		Save,

		// Special
		Debug,
		Unused,
		Unknown,  // Analyzed but purpose unclear — needs more investigation
		Helper,   // Utility/helper with no direct game-system role
	}

	public static class FunctionCategoryInfo
	{
		private static readonly Dictionary<FunctionCategory, Color> _colors = new()
		{
			{ FunctionCategory.None,         Color.FromRgb(0x80, 0x80, 0x80) },

			// Execution Backbone
			{ FunctionCategory.Init,         Color.FromRgb(0x6B, 0x7C, 0x93) },
			{ FunctionCategory.MainLoop,     Color.FromRgb(0x29, 0x80, 0xB9) },
			{ FunctionCategory.Interrupt,    Color.FromRgb(0xE7, 0x4C, 0x3C) },
			{ FunctionCategory.DMA,          Color.FromRgb(0xA0, 0x52, 0x2D) },

			// Input & Player
			{ FunctionCategory.Input,        Color.FromRgb(0x5B, 0x9B, 0xD5) },
			{ FunctionCategory.Player,       Color.FromRgb(0x27, 0xAE, 0x60) },

			// Graphics
			{ FunctionCategory.OAM,          Color.FromRgb(0x9B, 0x59, 0xB6) },
			{ FunctionCategory.VRAM,         Color.FromRgb(0x7D, 0x3C, 0x98) },
			{ FunctionCategory.Tilemap,      Color.FromRgb(0x1A, 0xBC, 0x9C) },
			{ FunctionCategory.Palette,      Color.FromRgb(0xE9, 0x1E, 0x63) },
			{ FunctionCategory.Scrolling,    Color.FromRgb(0x00, 0xBC, 0xD4) },
			{ FunctionCategory.Animation,    Color.FromRgb(0xF3, 0x9C, 0x12) },
			{ FunctionCategory.Effects,      Color.FromRgb(0xFF, 0x57, 0x22) },
			{ FunctionCategory.Mode7,        Color.FromRgb(0x00, 0x96, 0x88) },

			// Audio
			{ FunctionCategory.Music,        Color.FromRgb(0xFF, 0x40, 0x81) },
			{ FunctionCategory.SFX,          Color.FromRgb(0xFF, 0x98, 0x00) },

			// Gameplay Logic
			{ FunctionCategory.Physics,      Color.FromRgb(0x79, 0x55, 0x48) },
			{ FunctionCategory.Collision,    Color.FromRgb(0xC0, 0x39, 0x2B) },
			{ FunctionCategory.Entity,       Color.FromRgb(0x60, 0x7D, 0x8B) },
			{ FunctionCategory.Enemy,        Color.FromRgb(0xD3, 0x54, 0x00) },
			{ FunctionCategory.AI,           Color.FromRgb(0x8E, 0x44, 0xAD) },
			{ FunctionCategory.Camera,       Color.FromRgb(0x16, 0xA0, 0x85) },

			// Game Structure
			{ FunctionCategory.StateMachine, Color.FromRgb(0x21, 0x96, 0xF3) },
			{ FunctionCategory.GameState,    Color.FromRgb(0x15, 0x65, 0xC0) },
			{ FunctionCategory.Menu,         Color.FromRgb(0x00, 0x89, 0x7B) },
			{ FunctionCategory.HUD,          Color.FromRgb(0x43, 0xA0, 0x47) },
			{ FunctionCategory.LevelLoad,    Color.FromRgb(0x6D, 0x4C, 0x41) },
			{ FunctionCategory.Transition,   Color.FromRgb(0x54, 0x6E, 0x7A) },

			// Script & Dialogue
			{ FunctionCategory.Script,       Color.FromRgb(0xF0, 0x62, 0x92) },
			{ FunctionCategory.Dialogue,     Color.FromRgb(0xAB, 0x47, 0xBC) },

			// Data & Utility
			{ FunctionCategory.Math,         Color.FromRgb(0x78, 0x90, 0x9C) },
			{ FunctionCategory.RNG,          Color.FromRgb(0x4D, 0xB6, 0xAC) },
			{ FunctionCategory.Timer,        Color.FromRgb(0xFF, 0xB7, 0x4D) },
			{ FunctionCategory.Memory,       Color.FromRgb(0x90, 0xA4, 0xAE) },
			{ FunctionCategory.Text,         Color.FromRgb(0xA5, 0xD6, 0xA7) },
			{ FunctionCategory.Save,         Color.FromRgb(0xFF, 0xCA, 0x28) },

			// Special
			{ FunctionCategory.Debug,        Color.FromRgb(0xFF, 0x6F, 0x00) },
			{ FunctionCategory.Unused,       Color.FromRgb(0x9E, 0x9E, 0x9E) },
			{ FunctionCategory.Unknown,      Color.FromRgb(0xB7, 0x1C, 0x1C) },
			{ FunctionCategory.Helper,       Color.FromRgb(0x55, 0x7A, 0x95) },
		};

		private static readonly Dictionary<FunctionCategory, SolidColorBrush> _brushes = new();

		public static Color GetColor(FunctionCategory category)
		{
			return _colors.TryGetValue(category, out var c) ? c : _colors[FunctionCategory.None];
		}

		public static IBrush GetBrush(FunctionCategory category)
		{
			if(!_brushes.TryGetValue(category, out var brush)) {
				brush = new SolidColorBrush(GetColor(category));
				_brushes[category] = brush;
			}
			return brush;
		}

		/// <summary>Returns the display name, or empty string for None.</summary>
		public static string GetDisplay(FunctionCategory category)
			=> category == FunctionCategory.None ? "" : category.ToString();
	}
}
