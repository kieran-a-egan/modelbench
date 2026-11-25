from inventory.orders import place_order
from inventory.stock import is_low, receive_shipment


def main():
    receive_shipment({"widget": 40})
    total = place_order("widget", 10, 12.5)
    if is_low("widget"):
        print("reorder")
    print(total)
