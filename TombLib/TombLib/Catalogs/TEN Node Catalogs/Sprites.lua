local Timer = require("Engine.Timer")

-- !Name "Draw display sprite"
-- !Section "Sprites"
-- !Conditional "False"
-- !Description "Draw a display sprite on the frame that this node is active."
-- !Arguments "Newline, SpriteSlots, 61, Sprite sequence object ID"
-- !Arguments "Number, 19, [ 0 | 9999 | 0 ], Sprite ID in sprite sequence\nRange[0 to 9999]"
-- !Arguments "Color, 20, Color of sprite"
-- !Arguments "Newline, Number, 20, [ -1000 | 1000 | 2 ], Position X (%)\nRange [-1000 to 1000]\nVisible range [0 to 100]"
-- !Arguments "Number, 20, [ -1000 | 1000 | 2 ], Position Y (%)\nRange [-1000 to 1000]\nVisible range [0 to 100]"
-- !Arguments "Number, 20, [ 0 | 360 | 2 ], Rotation\nRange [0 to 360]"
-- !Arguments "Number, 20, {100}, [ 0 | 1000 | 2 ], Scale X (%)\nRange [0 to 1000]"
-- !Arguments "Number, 20, {100}, [ 0 | 1000 | 2 ], Scale Y (%)\nRange [0 to 1000]"
-- !Arguments "NewLine, Number, 15, [ -9999 | 9999 | 0 ], Draw priority\nRange [-9999 to 9999]. Negative values will draw sprite above strings."
-- !Arguments "Enumeration, 35, [ Center | Center Top | Center Bottom | Center Left | Center Right | Top Left | Top Right | Bottom Left | Bottom Right ], Align mode"
-- !Arguments "Enumeration, 22, [ Fit | Fill | Stretch ], Scale mode"
-- !Arguments "Enumeration, 28, [ Opaque | Alpha Test | Additive | No Z Test | Subtractive | Wireframe | Exclude | Screen | Lighten | Alphablend ], Blend mode"

LevelFuncs.Engine.Node.DisplaySprite = function(objectID, spriteID, color, posX, posY, rot, scaleX, scaleY, priority,
                                                alignMode, scaleMode, blendMode)
    local pos = TEN.Vec2(posX, posY)
    local scale = TEN.Vec2(scaleX, scaleY)
    local alignM = LevelFuncs.Engine.Node.GetDisplaySpriteAlignMode(alignMode)
    local scaleM = LevelFuncs.Engine.Node.GetDisplaySpriteScaleMode(scaleMode)
    local blendID = LevelFuncs.Engine.Node.GetBlendMode(blendMode)
    local sprite = TEN.DisplaySprite(objectID, spriteID, pos, rot, scale, color)
    sprite:Draw(priority, alignM, scaleM, blendID)
end

-- !Ignore
LevelFuncs.Engine.Node.DrawTimedDisplaySprite = function(dataName)
    local data = LevelVars.Engine.DisplaySpriteTimedData[dataName]
    if data == nil then
        return
    end

    local opacity = 1.0
    local fadeTime = math.min(1.0, data.time / ((data.fadeIn and data.fadeOut) and 2.0 or 1.0))
    if data.fadeIn then
        opacity = math.min(opacity, data.elapsed / fadeTime)
    end

    if data.fadeOut then
        opacity = math.min(opacity, (data.time - data.elapsed) / fadeTime)
    end

    local color = TEN.Color(data.color.r, data.color.g, data.color.b, math.floor(data.color.a * opacity + 0.5))
    local sprite = TEN.DisplaySprite(data.objectID, data.spriteID, data.pos, data.rot, data.scale, color)
    sprite:Draw(data.priority, data.alignMode, data.scaleMode,
        (data.fadeIn or data.fadeOut) and TEN.Effects.BlendID.ALPHABLEND or data.blendMode)

    data.elapsed = data.elapsed + (1.0 / 30.0)
    if data.elapsed > data.time then
        Timer.Delete(dataName)
        LevelVars.Engine.DisplaySpriteTimedData[dataName] = nil
    end
end

-- !Name "Draw display sprite for time span"
-- !Section "Sprites"
-- !Conditional "False"
-- !Description "Draw a display sprite for the specified time. Fade effects use AlphaBlend mode."
-- !Arguments "Newline, SpriteSlots, 61, Sprite sequence object ID"
-- !Arguments "Number, 19, [ 0 | 9999 | 0 ], Sprite ID in sprite sequence\nRange[0 to 9999]"
-- !Arguments "Color, 20, Color of sprite"
-- !Arguments "Newline, Number, 20, [ -1000 | 1000 | 2 ], Position X (%)\nRange [-1000 to 1000]\nVisible range [0 to 100]"
-- !Arguments "Number, 20, [ -1000 | 1000 | 2 ], Position Y (%)\nRange [-1000 to 1000]\nVisible range [0 to 100]"
-- !Arguments "Number, 20, [ 0 | 360 | 2 ], Rotation\nRange [0 to 360]"
-- !Arguments "Number, 20, {100}, [ 0 | 1000 | 2 ], Scale X (%)\nRange [0 to 1000]"
-- !Arguments "Number, 20, {100}, [ 0 | 1000 | 2 ], Scale Y (%)\nRange [0 to 1000]"
-- !Arguments "NewLine, Number, 15, [ -9999 | 9999 | 0 ], Draw priority\nRange [-9999 to 9999]. Negative values will draw sprite above strings."
-- !Arguments "Enumeration, 35, [ Center | Center Top | Center Bottom | Center Left | Center Right | Top Left | Top Right | Bottom Left | Bottom Right ], Align mode"
-- !Arguments "Enumeration, 22, [ Fit | Fill | Stretch ], Scale mode"
-- !Arguments "Enumeration, 28, [ Opaque | Alpha Test | Additive | No Z Test | Subtractive | Wireframe | Exclude | Screen | Lighten | Alphablend ], Blend mode"
-- !Arguments "NewLine, Numerical, [ 0.1 | 65535 | 2 | 0.1 | 1 ], {1}, 40, Time (in seconds)"
-- !Arguments "30, Boolean, {false}, Fade in"
-- !Arguments "30, Boolean, {false}, Fade out"

LevelFuncs.Engine.Node.DisplaySpriteForTimespan = function(objectID, spriteID, color, posX, posY, rot, scaleX, scaleY, priority,
                                                           alignMode, scaleMode, blendMode, time, fadeIn, fadeOut)
    LevelVars.Engine.DisplaySpriteTimedData = LevelVars.Engine.DisplaySpriteTimedData or {}
    LevelVars.Engine.DisplaySpriteTimedIndex = (LevelVars.Engine.DisplaySpriteTimedIndex or 0) + 1

    local dataName = "display_sprite_timed_" .. LevelVars.Engine.DisplaySpriteTimedIndex
    LevelVars.Engine.DisplaySpriteTimedData[dataName] =
    {
        objectID = objectID,
        spriteID = spriteID,
        color = color,
        pos = TEN.Vec2(posX, posY),
        rot = rot,
        scale = TEN.Vec2(scaleX, scaleY),
        priority = priority,
        alignMode = LevelFuncs.Engine.Node.GetDisplaySpriteAlignMode(alignMode),
        scaleMode = LevelFuncs.Engine.Node.GetDisplaySpriteScaleMode(scaleMode),
        blendMode = LevelFuncs.Engine.Node.GetBlendMode(blendMode),
        time = time,
        fadeIn = fadeIn,
        fadeOut = fadeOut,
        elapsed = 0.0
    }

    LevelFuncs.Engine.Node.DrawTimedDisplaySprite(dataName)
    Timer.Create(dataName, 1.0 / 30.0, true, false, LevelFuncs.Engine.Node.DrawTimedDisplaySprite, dataName):Start()
end

-- !Name "Draw display sprite at mouse position"
-- !Section "Sprites"
-- !Conditional "False"
-- !Description "Draw a display sprite under the mouse cursor."
-- !Arguments "Newline, SpriteSlots, 61, Sprite sequence object ID"
-- !Arguments "Number, 19, [ 0 | 9999 | 0 ], Sprite ID in sprite sequence\nRange[0 to 9999]"
-- !Arguments "Color, 20, Color of sprite"
-- !Arguments "Newline, Number, 34, [ 0 | 360 | 2 ], Rotation\nRange [0 to 360]"
-- !Arguments "Number, 33, {100}, [ 0 | 1000 | 2 ], Scale X (%)\nRange [0 to 1000]"
-- !Arguments "Number, 33, {100}, [ 0 | 1000 | 2 ], Scale Y (%)\nRange [0 to 1000]"
-- !Arguments "NewLine, Number, 15, [ -9999 | 9999 | 0 ], Draw priority\nRange [-9999 to 9999]. Negative values will draw sprite above strings."
-- !Arguments "Enumeration, 35, [ Center | Center Top | Center Bottom | Center Left | Center Right | Top Left | Top Right | Bottom Left | Bottom Right ], Align mode"
-- !Arguments "Enumeration, 22, [ Fit | Fill | Stretch ], Scale mode"
-- !Arguments "Enumeration, 28, [ Opaque | Alpha Test | Additive | No Z Test | Subtractive | Wireframe | Exclude | Screen | Lighten | Alphablend ], Blend mode"

LevelFuncs.Engine.Node.DisplaySpriteAtMousePosition = function(objectID, spriteID, color, rot, scaleX, scaleY, priority,
                                                alignMode, scaleMode, blendMode)
    local pos = TEN.Input.GetCursorDisplayPosition()
    local scale = TEN.Vec2(scaleX, scaleY)
    local alignM = LevelFuncs.Engine.Node.GetDisplaySpriteAlignMode(alignMode)
    local scaleM = LevelFuncs.Engine.Node.GetDisplaySpriteScaleMode(scaleMode)
    local blendID = LevelFuncs.Engine.Node.GetBlendMode(blendMode)
    local sprite = TEN.DisplaySprite(objectID, spriteID, pos, rot, scale, color)
    sprite:Draw(priority, alignM, scaleM, blendID)
end
