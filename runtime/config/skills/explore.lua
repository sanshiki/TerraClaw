-- explore.lua
-- Explore by flying in a direction, observing interesting features.
return {
    name = "explore",
    description = "Fly in a direction and observe surroundings",

    execute = function(ctx, params)
        local direction = params.direction or "right"
        local steps = params.steps or 5
        local step_size = params.step_size or 320 -- 20 tiles per step

        local dx = 0
        if direction == "right" then dx = 1
        elseif direction == "left" then dx = -1 end

        local results = {}
        for i = 1, steps do
            local move_result = ctx:send_action("move_to", {
                x = params.start_x + dx * i * step_size,
                y = params.start_y or 0,
                speed = 8.0,
                arrival_radius = 32.0,
            })
            ctx:wait(200)
            results[i] = move_result
        end

        return { success = true, message = "Explored " .. direction, steps = steps }
    end,
}
