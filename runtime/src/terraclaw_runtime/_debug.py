"""Debug output control — global verbose flag."""
VERBOSE = False


def dprint(*args, **kwargs):
    if VERBOSE:
        print(*args, **kwargs)
