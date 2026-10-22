-- !Name "Draw text"
-- !Section "Text"
-- !Description "Draws simple label on a screen for one frame."
-- !Arguments "NewLine, 70, String, Text or string key" "Numerical, 15, X position, [ 0 | 100 ]" "Numerical, 15, Y position, [ 0 | 100 ]"
-- !Arguments "NewLine, Enumeration, 20, [ Left | Center | Right ], Horizontal alignment"
-- !Arguments "Enumeration, 38, [ Flat | Shadow | Blinking | Shadow + Blinking ], Effects"
-- !Arguments "Color, 14, {TEN.Color(255,255,255)}, Text color" 
-- !Arguments "Numerical, 14, {1}, [ 0 | 9 | 2 | 0.1 ], Scale" "Numerical, 14, {1}, [ 0 | 1 | 2 | 0.1 ], Transparency"

LevelFuncs.Engine.Node.DrawText = function(text, x, y, alignment, effects, color, scale, alpha)
	local str = LevelFuncs.Engine.Node.GenerateString(text, x, y, scale, alignment, effects, color, alpha)
	TEN.Strings.ShowString(str, 1 / 30)
end

local Timer = require("Engine.Timer")
local typewriterId = 0

-- !Ignore
LevelFuncs.Engine.Node.UpdateTypewriterText = function(data)
	data.frames = data.frames + 1
	local byte = string.byte(data.text, data.index + 1)
	data.index = data.index + (byte < 0x80 and 1 or byte < 0xE0 and 2 or byte < 0xF0 and 3 or 4)

	local str = LevelFuncs.Engine.Node.GenerateString(string.sub(data.text, 1, data.index), data.x, data.y, data.scale, data.alignment, data.effects, data.color, data.alpha)
	if (data.index >= #data.text) then
		str = LevelFuncs.Engine.Node.GenerateString(data.text, data.x, data.y, data.scale, data.alignment, data.effects, data.color, data.alpha)
		TEN.Strings.ShowString(str, data.time - data.frames / 22)
		Timer.Delete(data.name)
	else
		TEN.Strings.ShowString(str, 1 / 22)
	end
end

-- !Name "Draw text for a time span"
-- !Section "Text"
-- !Description "Draws simple label on a screen for a specified time span."
-- !Arguments "Numerical, 20, [ 0 | 180 | 2 | 0.25 | 1 ], Time span in seconds"
-- !Arguments "NewLine, 70, String, Text or string key" "Numerical, 15, X position, [ 0 | 100 ]" "Numerical, 15, Y position, [ 0 | 100 ]"
-- !Arguments "NewLine, Enumeration, 20, [ Left | Center | Right ], Horizontal alignment"
-- !Arguments "Enumeration, 38, [ Flat | Shadow | Blinking | Shadow + Blinking ], Effects"
-- !Arguments "Color, 14, {TEN.Color(255,255,255)}, Text color" 
-- !Arguments "Numerical, 14, {1}, [ 0 | 9 | 2 | 0.1 ], Scale" "Numerical, 14, {1}, [ 0 | 1 | 2 | 0.1 ], Transparency"
-- !Arguments "NewLine, Boolean, Typewriter effect"

LevelFuncs.Engine.Node.DrawTextForTimespan = function(time, text, x, y, alignment, shadow, color, scale, alpha, typewriter)
	if (typewriter) then
		if (TEN.Flow.IsStringPresent(text)) then
			text = TEN.Flow.GetString(text)
		end

		if (time <= 0 or #text == 0) then
			local str = LevelFuncs.Engine.Node.GenerateString(text, x, y, scale, alignment, shadow, color, alpha)
			TEN.Strings.ShowString(str, time)
			return
		end

		typewriterId = typewriterId + 1
		local data = { name = "typewriter_" .. typewriterId, text = text, x = x, y = y, alignment = alignment, effects = shadow, color = color, scale = scale, alpha = alpha, time = time, index = 0, frames = 0 }
		local timer = Timer.Create(data.name, 1 / 22, true, false, LevelFuncs.Engine.Node.UpdateTypewriterText, data)
		timer:Start()
		return
	end

	local str = LevelFuncs.Engine.Node.GenerateString(text, x, y, scale, alignment, shadow, color, alpha)
	TEN.Strings.ShowString(str, time)
end

-- !Name "Draw subtitle for the voice track"
-- !Section "Text"
-- !Description "If voice track is active and subtitle file with the same name as voice track exists in same folder, try to display a subtitle."
-- !Arguments "Numerical, 15, X position, {50}, [ 0 | 100 ]" "Numerical, 15, Y position, {80}, [ 0 | 100 ]"
-- !Arguments "NewLine, Enumeration, 20, [ Left | Center | Right ], Horizontal alignment"
-- !Arguments "Enumeration, 40, [ Flat | Shadow | Blinking | Shadow + Blinking ], Effects"
-- !Arguments "Color, 20, {TEN.Color(255,255,255)}, Text color" "Numerical, 20, {1}, [ 0 | 9 | 2 | 0.1 ], Scale"

LevelFuncs.Engine.Node.DrawSubtitle = function(x, y, alignment, shadow, color, scale)
	local text = TEN.Sound.GetCurrentSubtitle()

	if (text ~= nil) then
		local str = LevelFuncs.Engine.Node.GenerateString(text, x, y, scale, alignment, shadow, color)
		TEN.Strings.ShowString(str, 1 / 30)
	end
end
