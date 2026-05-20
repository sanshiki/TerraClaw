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

        -- Move to position (non-blocking, get action_id)
        local move_id = ctx:start_action("move_to", {
            x = px,
            y = py,
            speed = 6.0,
            arrival_radius = 16.0,
        })

        -- Wait for movement to complete before breaking
        ctx:wait_for(move_id)

        -- Break the tile (fire-and-forget, no need to wait)
        ctx:start_action("break_tile", {
            tx = tx,
            ty = ty,
        })

        return {
            success = true,
            message = "Mined tile at (" .. tx .. ", " .. ty .. ")",
        }
    end,
}
