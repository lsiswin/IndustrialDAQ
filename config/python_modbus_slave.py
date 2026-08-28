import random
import time
import threading
import logging
from pymodbus.server import StartTcpServer
from pymodbus.datastore import ModbusSequentialDataBlock, ModbusSlaveContext, ModbusServerContext
from pymodbus.payload import BinaryPayloadBuilder, BinaryPayloadDecoder
from pymodbus.constants import Endian

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# ==========================================
# 配置区：匹配 C# ModbusTcpDriver 的解析逻辑
# ==========================================
# C# 驱动使用的是全 Little Endian (低字节在前，低字在前)
# 为了和 C# 的提取方式 (regs & 0xFF, regs >> 8) 匹配，我们需要在 Python 端将 Byte Order 设为 BIG (不交换单字内的字节)，Word Order 设为 LITTLE (低字在寄存器0)
BYTE_ORDER = Endian.BIG
WORD_ORDER = Endian.LITTLE

# 模拟参数
TANK_CAPACITY = 1000.0          # 罐体容量 L
FILL_RATE = 80.0                # 灌装速度 L/s，便于测试时快速跨越报警阈值
DRAIN_RATE = 100.0              # 排放速度 L/s
CONVEYOR_MAX_SPEED = 15.0       # 传送带最大速度 m/min
AUTO_DEMO = True                # 无需客户端写启动命令，自动循环灌装用于报警测试

class ProductionLineSimulator:
    """产线模拟器：模拟真实的罐装生产线运行"""
    
    def __init__(self):
        # 过程数据 (Input Registers / Discrete Inputs) - 只读 (Read)
        self.level = 0.0            # 当前液位 (30001)
        self.speed = 0.0            # 实际传送速度 (30003)
        self.total_count = 0        # 总产量 (30005)
        self.running = False        # 产线运行 (10001)
        self.alarm_active = False   # 报警状态 (10002)
        self.valve_open = False     # 灌装阀 (10003)
        self.conveyor_running = False # 传送带运行 (10004)
        
        # 控制指令和设定参数 - 可写 (Write)
        self.cmd_start = False      # 启动 (00001)
        self.cmd_stop = False       # 停止 (00002)
        self.cmd_auto = AUTO_DEMO   # 自动模式默认开启，保证测试数据持续变化
        self.cmd_estop = False      # 急停 (00004)
        self.set_level = 500.0      # 设定液位 (40001)
        self.set_speed = 10.0       # 设定速度 (40003)
        
        # 内部状态
        self.filling = False
        
    def update(self, dt=1.0):
        """状态更新逻辑"""
        # 1. 响应主站写入的控制指令；测试模式下默认保持自动运行。
        if self.cmd_estop:
            self.running = False
            self.alarm_active = True
        elif self.cmd_stop:
            self.running = False
            self.alarm_active = False
        elif self.cmd_start or self.cmd_auto or AUTO_DEMO:
            self.running = True
            self.alarm_active = False

        # 急停状态锁定 (物理模拟超出液位)
        if self.level > 950:
            self.alarm_active = True
            self.running = False
            
        # 2. 传送带速度与状态控制
        if self.running and not self.alarm_active:
            target = self.set_speed
            if self.speed < target:
                self.speed = min(self.speed + 2.0 * dt, target)
            elif self.speed > target:
                self.speed = max(self.speed - 2.0 * dt, target)
        else:
            if self.speed > 0:
                self.speed = max(self.speed - 5.0 * dt, 0.0)
        
        self.conveyor_running = self.speed > 0.1
                
        # 3. 灌装逻辑
        if self.running and not self.filling and self.level < self.set_level:
            self.filling = True
            self.valve_open = True
            
        if self.filling:
            # 始终使用主站写入的设定液位；自动演示只负责自动运行，不能覆盖工艺设定值。
            target_level = max(0.0, min(self.set_level, TANK_CAPACITY))
            self.level = min(self.level + FILL_RATE * dt, target_level)
            if self.level >= target_level:
                self.filling = False
                self.valve_open = False
                self.total_count += 1
                    
        # 4. 传送带带走液体消耗逻辑
        if not self.filling and self.level > 0:
            self.level = max(self.level - DRAIN_RATE * dt, 0)
            
        # 随机波动增加真实感
        self.level += random.uniform(-1.0, 1.0)
        self.level = max(0, min(self.level, TANK_CAPACITY))

def float_to_registers(value):
    """将浮点数转换为两个 16 位寄存器"""
    builder = BinaryPayloadBuilder(byteorder=BYTE_ORDER, wordorder=WORD_ORDER)
    builder.add_32bit_float(value)
    return builder.to_registers()

def int_to_registers(value):
    """将整数转换为两个 16 位寄存器"""
    builder = BinaryPayloadBuilder(byteorder=BYTE_ORDER, wordorder=WORD_ORDER)
    builder.add_32bit_int(value)
    return builder.to_registers()

def pack_bits(*states):
    """将多个布尔状态打包到一个 Holding Register，与 JSON 的 bitIndex 对齐。"""
    register_value = 0
    for bit_index, state in enumerate(states):
        if state:
            register_value |= 1 << bit_index
    return register_value

def simulate_data(context, simulator):
    """后台线程：动态更新与读取 Modbus 数据区"""
    slave_id = 1
    store = context[slave_id]
    
    # JSON 当前全部使用 Holding Register：40001 对应内部偏移 0。
    store.setValues(3, 0, float_to_registers(simulator.set_level))
    store.setValues(3, 2, float_to_registers(simulator.set_speed))
    store.setValues(3, 12, [pack_bits(False, False, AUTO_DEMO, False)])
    
    while True:
        time.sleep(1.0)
        try:
            # ==========================================
            # 接收端 (Write Access): 读取主站写入的数据
            # ==========================================
            
            # 40013 对应偏移 12，四个控制命令由 bitIndex 0~3 表示。
            control_word = store.getValues(3, 12, 1)[0]
            simulator.cmd_start = bool(control_word & 0x0001)
            simulator.cmd_stop = bool(control_word & 0x0002)
            simulator.cmd_auto = bool(control_word & 0x0004) or AUTO_DEMO
            simulator.cmd_estop = bool(control_word & 0x0008)
            
            # 读取 Holding Registers (40001 - 40004) -> Pymodbus fc=3, start=0, count=4
            hr_values = store.getValues(3, 0, 4)
            decoder = BinaryPayloadDecoder.fromRegisters(hr_values, byteorder=BYTE_ORDER, wordorder=WORD_ORDER)
            # 顺序需严格匹配地址: 40001 是 SetLevel, 40003 是 SetSpeed
            simulator.set_level = decoder.decode_32bit_float()
            simulator.set_speed = decoder.decode_32bit_float()

            # ==========================================
            # 物理模拟更新
            # ==========================================
            simulator.update(1.0)
            
            # ==========================================
            # 发送端 (Read Access): 更新物理状态供主站读取
            # ==========================================
            
            # 严格匹配 production-line.json 的 Holding Register 偏移。
            store.setValues(3, 5, float_to_registers(simulator.level))      # 40006-40007: ActualLevel
            store.setValues(3, 7, float_to_registers(simulator.speed))      # 40008-40009: ActualSpeed
            store.setValues(3, 9, int_to_registers(simulator.total_count))  # 40010-40011: TotalCount
            store.setValues(3, 13, [pack_bits(
                simulator.running,
                simulator.alarm_active,
                simulator.valve_open,
                simulator.conveyor_running)])                              # 40014: 状态位
            
            logger.info(f"状态: 运行={simulator.running}, 液位={simulator.level:.1f}L, "
                       f"设定液位={simulator.set_level:.1f}L, 实际速度={simulator.speed:.1f}m/min, "
                       f"设定速度={simulator.set_speed:.1f}m/min, 产量={simulator.total_count}")
                       
        except Exception as e:
            logger.error(f"数据更新出错: {e}")

def run_slave():
    simulator = ProductionLineSimulator()
    
    # zero_mode=True 确保 00001 映射为内部地址 0，完美匹配 C# 驱动计算
    slave_context = ModbusSlaveContext(
        di=ModbusSequentialDataBlock(0, [0] * 100),   # 1x: Discrete Inputs (主站只读)
        co=ModbusSequentialDataBlock(0, [0] * 100),   # 0x: Coils (主站可写)
        ir=ModbusSequentialDataBlock(0, [0] * 100),   # 3x: Input Registers (主站只读)
        hr=ModbusSequentialDataBlock(0, [0] * 100),   # 4x: Holding Registers (主站可写)
        zero_mode=True
    )
    
    context = ModbusServerContext(slaves={1: slave_context}, single=False)
    
    t = threading.Thread(target=simulate_data, args=(context, simulator))
    t.daemon = True
    t.start()
    
    logger.info("=" * 50)
    logger.info("Modbus TCP Slave 模拟器启动")
    logger.info("监听: 127.0.0.1:502")
    logger.info("寄存器布局: production-line.json Holding Register 40001-40014")
    logger.info("字节序: 低字在前，匹配 C# ModbusTcpDriver")
    logger.info("自动演示: 自动启停循环，最高液位严格服从 SetLevel 写入值")
    logger.info("=" * 50)
    
    StartTcpServer(context=context, address=("127.0.0.1", 502))

if __name__ == "__main__":
    run_slave()
