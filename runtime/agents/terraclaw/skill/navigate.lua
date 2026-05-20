-- navigate.lua
-- Navigate to target world coordinates and report arrival.
return {
    name = "navigate",
    description = "Fly to target world coordinates and report when arrived",

    execute = function(ctx, params)
        local target_x = params.target_x or params.x or 0
        local target_y = params.target_y or params.y or 0

        local aid = ctx:start_action("move_to", {
            x = target_x,
            y = target_y,
            speed = 6.0,
            arrival_radius = 16.0,
        })

        -- Wait for movement to complete
        local result = ctx:wait_for(aid)

        return { success = true, message = "Arrived at destination", result = result }
    end,
}
