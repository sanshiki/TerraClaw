-- mine_vein.lua
-- Move to target coordinates and break surrounding tiles.
return {
    name = "mine_vein",
    description = "Move to a tile position and break the tile there",

    execute = function(ctx, params)
        local tx = params.tx or params.target_x or 0
        local ty = params.ty or params.target_y or 0

        -- Convert tile to pixel coordinates
        local px = tx * 16
        local py = ty * 16

        -- Move to position
        ctx:send_action("move_to", {
            x = px,
            y = py,
            speed = 6.0,
            arrival_radius = 16.0,
        })

        ctx:wait(300)

        -- Break the tile
        local break_result = ctx:send_action("break_tile", {
            tx = tx,
            ty = ty,
        })

        return {
            success = true,
            message = "Mined tile at (" .. tx .. ", " .. ty .. ")",
            result = break_result,
        }
    end,
}
